using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using LiCvWriter.Infrastructure.Llm;
using Microsoft.Extensions.DependencyInjection;

namespace LiCvWriter.Web.Services;

/// <summary>
/// Drives a sequential benchmark across an arbitrary list of local models for a
/// specific provider, persists the ranked outcome through
/// <see cref="WorkspaceSession"/>, and surfaces live progress to the UI via
/// <see cref="Changed"/>. A single run is active at a time; <see cref="Cancel"/>
/// signals the in-flight model to stop and short-circuits the remaining queue.
/// </summary>
public sealed class ModelBenchmarkCoordinator(
    IServiceScopeFactory scopeFactory,
    WorkspaceSession workspace,
    OperationStatusService operations,
    OllamaOptions ollamaOptions,
    FoundryOptions foundryOptions,
    TimeProvider timeProvider)
{
    private readonly object gate = new();
    private ModelBenchmarkSession? current;
    private CancellationTokenSource? activeCts;

    public event Action? Changed;

    public ModelBenchmarkSession? Current
    {
        get { lock (gate) { return current; } }
    }

    public ModelBenchmarkSession? Last => workspace.LastBenchmarkSession;

    public bool IsRunning => Current is { IsRunning: true };

    public Task StartAsync(IReadOnlyList<string> models)
        => StartAsync(LlmProviderKind.Ollama, models);

    public Task StartAsync(LlmProviderKind provider, IReadOnlyList<string> models)
        => StartAsync(provider, models, downloadMissingModels: false, removeTooLargeModelsAfterBenchmark: false);

    public Task StartAsync(
        LlmProviderKind provider,
        IReadOnlyList<string> models,
        bool downloadMissingModels,
        bool removeTooLargeModelsAfterBenchmark)
    {
        ArgumentNullException.ThrowIfNull(models);

        var trimmed = models
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("At least one installed model is required to start a benchmark.");
        }

        CancellationToken token;
        lock (gate)
        {
            if (current is { IsRunning: true })
            {
                throw new InvalidOperationException("A benchmark run is already in progress.");
            }

            activeCts = new CancellationTokenSource();
            token = activeCts.Token;
            current = new ModelBenchmarkSession(
                StartedUtc: timeProvider.GetUtcNow(),
                CompletedUtc: null,
                IsRunning: true,
                IsCancelled: false,
                CompletedCount: 0,
                TotalCount: trimmed.Length,
                CurrentModel: trimmed[0],
                Results: Array.Empty<ModelBenchmarkResult>(),
                Provider: provider)
            {
                CurrentPhase = ModelBenchmarkRunPhase.Preparing,
                CurrentDetail = "Queue initialized for benchmark run.",
                TotalFixtureCount = ModelBenchmarkFixtures.DefaultSuite.Count
            };
        }

        Changed?.Invoke();
        return RunAsync(provider, trimmed, downloadMissingModels, removeTooLargeModelsAfterBenchmark, token);
    }

    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (gate)
        {
            cts = activeCts;
        }

        cts?.Cancel();
    }

    private async Task RunAsync(
        LlmProviderKind provider,
        IReadOnlyList<string> models,
        bool downloadMissingModels,
        bool removeTooLargeModelsAfterBenchmark,
        CancellationToken cancellationToken)
    {
        var results = new List<ModelBenchmarkResult>(models.Count);
        var perModelTimeoutSeconds = ollamaOptions.MaxOperationSeconds;
        var cancelled = false;

        for (var index = 0; index < models.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var model = models[index];
            ReportLiveProgress(
                model,
                index,
                models.Count,
                results.ToArray(),
                new ModelBenchmarkProgress(
                    Model: model,
                    Phase: ModelBenchmarkRunPhase.Preparing,
                    Detail: "Queue slot reserved for benchmark run.",
                    CompletedFixtureCount: 0,
                    TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count));

            ModelBenchmarkResult result;
            using var perModelCts = perModelTimeoutSeconds > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;

            if (perModelCts is not null)
            {
                perModelCts.CancelAfter(TimeSpan.FromSeconds(perModelTimeoutSeconds));
            }

            var effectiveToken = perModelCts?.Token ?? cancellationToken;

            try
            {
                using var scope = scopeFactory.CreateScope();
                FoundryAccelerationSnapshot? foundryAcceleration = null;
                if (provider == LlmProviderKind.Foundry)
                {
                    foundryAcceleration = await EnsureFoundryModelReadyAsync(
                        scope.ServiceProvider,
                        model,
                        index,
                        models.Count,
                        downloadMissingModels,
                        progress => ReportLiveProgress(model, index, models.Count, results.ToArray(), progress),
                        cancellationToken);
                }

                var benchmarkService = ResolveBenchmarkService(scope.ServiceProvider, provider);
                result = await benchmarkService.RunSingleAsync(
                    model,
                    progress => ReportLiveProgress(model, index, models.Count, results.ToArray(), progress),
                    effectiveToken);
                result = result with { Provider = provider };

                if (foundryAcceleration is not null)
                {
                    result = ApplyFoundryAccelerationNotes(result, foundryAcceleration);
                }

                if (provider == LlmProviderKind.Foundry)
                {
                    ReportLiveProgress(
                        model,
                        index,
                        models.Count,
                        results.ToArray(),
                        new ModelBenchmarkProgress(
                            Model: model,
                            Phase: ModelBenchmarkRunPhase.Cleanup,
                            Detail: "Applying Foundry cleanup and availability refresh.",
                            CompletedFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count,
                            TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count));

                    result = await FinalizeFoundryBenchmarkResultAsync(
                        scope.ServiceProvider,
                        model,
                        result,
                        removeTooLargeModelsAfterBenchmark,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            catch (OperationCanceledException)
            {
                // Per-model timeout — record as a failure and continue the queue.
                result = new ModelBenchmarkResult(
                    Model: model,
                    Rank: 0,
                    OverallScore: 0.0,
                    QualityScore: 0.0,
                    DecodeTokensPerSecond: null,
                    LoadDuration: null,
                    TotalDuration: null,
                    Fit: OllamaCapacityFit.Unknown,
                    Notes: Array.Empty<string>(),
                    FailedReason: $"Timed out after {perModelTimeoutSeconds}s.",
                    Provider: provider,
                    FixtureResults: Array.Empty<ModelBenchmarkFixtureResult>());
            }
            catch (Exception exception)
            {
                result = new ModelBenchmarkResult(
                    Model: model,
                    Rank: 0,
                    OverallScore: 0.0,
                    QualityScore: 0.0,
                    DecodeTokensPerSecond: null,
                    LoadDuration: null,
                    TotalDuration: null,
                    Fit: OllamaCapacityFit.Unknown,
                    Notes: Array.Empty<string>(),
                    FailedReason: exception.Message,
                    Provider: provider,
                    FixtureResults: Array.Empty<ModelBenchmarkFixtureResult>());
            }

            results.Add(result);
            EmitPerModelActivity(result);
            ReportLiveProgress(
                index + 1 < models.Count ? models[index + 1] : null,
                index + 1,
                models.Count,
                results.ToArray(),
                new ModelBenchmarkProgress(
                    Model: model,
                    Phase: ModelBenchmarkRunPhase.Finalizing,
                    Detail: "Locking in partial rankings and moving to the next model.",
                    CompletedFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count,
                    TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count));
        }

        var ranked = RankResults(results);
        var session = new ModelBenchmarkSession(
            StartedUtc: current?.StartedUtc ?? timeProvider.GetUtcNow(),
            CompletedUtc: timeProvider.GetUtcNow(),
            IsRunning: false,
            IsCancelled: cancelled,
            CompletedCount: ranked.Count,
            TotalCount: models.Count,
            CurrentModel: null,
            Results: ranked,
            Provider: provider)
        {
            CurrentPhase = cancelled ? ModelBenchmarkRunPhase.Cancelled : ModelBenchmarkRunPhase.Completed,
            CurrentDetail = cancelled ? "Benchmark run cancelled." : "Benchmark run completed.",
            CompletedFixtureCount = ModelBenchmarkFixtures.DefaultSuite.Count,
            TotalFixtureCount = ModelBenchmarkFixtures.DefaultSuite.Count
        };

        lock (gate)
        {
            current = session;
            activeCts?.Dispose();
            activeCts = null;
        }

        workspace.SetLastBenchmarkSession(session);
        Changed?.Invoke();
    }

    private void UpdateProgress(string? currentModel, IReadOnlyList<ModelBenchmarkResult> partialResults, int completedCount, ModelBenchmarkProgress progress)
    {
        var rankedPartialResults = RankResults(partialResults);

        lock (gate)
        {
            if (current is null)
            {
                return;
            }

            current = current with
            {
                CurrentModel = currentModel,
                CompletedCount = completedCount,
                Results = rankedPartialResults,
                CurrentPhase = progress.Phase,
                CurrentDetail = progress.Detail,
                CurrentFixtureId = progress.CurrentFixtureId,
                CurrentFixtureDisplayName = progress.CurrentFixtureDisplayName,
                CurrentPromptId = progress.CurrentPromptId,
                CurrentFixtureNumber = progress.CurrentFixtureNumber,
                CompletedFixtureCount = progress.CompletedFixtureCount,
                TotalFixtureCount = progress.TotalFixtureCount
            };
        }

        Changed?.Invoke();
    }

    private void ReportLiveProgress(
        string? currentModel,
        int completedCount,
        int totalCount,
        IReadOnlyList<ModelBenchmarkResult> partialResults,
        ModelBenchmarkProgress progress)
    {
        UpdateProgress(currentModel, partialResults, completedCount, progress);
        operations.UpdateCurrent(GetProgressMessage(currentModel ?? progress.Model, completedCount, totalCount, progress), progress.Detail);
    }

    private static string GetProgressMessage(string model, int completedCount, int totalCount, ModelBenchmarkProgress progress)
        => progress.Phase switch
        {
            ModelBenchmarkRunPhase.Preparing => $"Preparing {model} ({completedCount + 1}/{totalCount})",
            ModelBenchmarkRunPhase.Cleanup => $"Cleaning up {model} ({completedCount + 1}/{totalCount})",
            _ => $"Benchmarking {model} ({completedCount + 1}/{totalCount})"
        };

    private void EmitPerModelActivity(ModelBenchmarkResult result)
    {
        var providerLabel = result.Provider switch
        {
            LlmProviderKind.Foundry => "Foundry",
            _ => "Ollama"
        };

        if (!result.Succeeded)
        {
            operations.Error($"{providerLabel} benchmark failed: {result.Model}", result.FailedReason);
            return;
        }

        var speed = result.DecodeTokensPerSecond is { } tps ? $"{tps:0.0} tok/s" : "speed n/a";
        var detail = $"overall {result.OverallScore:0.00} • quality {result.QualityScore:0.00} • {speed} • {result.Fit}";
        operations.Info($"Benchmarked {providerLabel} model {result.Model}", detail);
    }

    private OllamaModelBenchmarkService ResolveBenchmarkService(IServiceProvider serviceProvider, LlmProviderKind provider)
    {
        if (provider != LlmProviderKind.Foundry)
        {
            return serviceProvider.GetRequiredService<OllamaModelBenchmarkService>();
        }

        var foundryClient = serviceProvider.GetRequiredService<FoundryLlmClient>();
        var capacityProbe = new OllamaCapacityProbe(foundryClient, ollamaOptions);
        return new OllamaModelBenchmarkService(capacityProbe, foundryClient, ollamaOptions);
    }

    private async Task<FoundryAccelerationSnapshot> EnsureFoundryModelReadyAsync(
        IServiceProvider serviceProvider,
        string model,
        int index,
        int totalCount,
        bool downloadMissingModels,
        Action<ModelBenchmarkProgress>? progress,
        CancellationToken cancellationToken)
    {
        var catalogClient = serviceProvider.GetRequiredService<IFoundryCatalogClient>();
        var snapshot = await catalogClient.GetSnapshotAsync(cancellationToken);
        snapshot = await EnsureFoundryAccelerationReadyAsync(catalogClient, model, index, totalCount, snapshot, progress, cancellationToken);

        if (snapshot.Availability.AvailableModels.Any(alias => alias.Equals(model, StringComparison.OrdinalIgnoreCase)))
        {
            return snapshot.Acceleration;
        }

        if (!downloadMissingModels)
        {
            throw new InvalidOperationException($"The Foundry model '{model}' is not cached locally. Download it first or run benchmark with download enabled.");
        }

        progress?.Invoke(new ModelBenchmarkProgress(
            Model: model,
            Phase: ModelBenchmarkRunPhase.Preparing,
            Detail: "Downloading model before benchmark.",
            CompletedFixtureCount: 0,
            TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count));

        await catalogClient.DownloadModelAsync(
            model,
            downloadPercent => progress?.Invoke(new ModelBenchmarkProgress(
                Model: model,
                Phase: ModelBenchmarkRunPhase.Preparing,
                Detail: $"Downloading model before benchmark: {downloadPercent:0.0}%",
                CompletedFixtureCount: 0,
                TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count)),
            cancellationToken);

        var refreshedSnapshot = await catalogClient.GetSnapshotAsync(cancellationToken);
        return refreshedSnapshot.Acceleration;
    }

    private async Task<FoundryCatalogSnapshot> EnsureFoundryAccelerationReadyAsync(
        IFoundryCatalogClient catalogClient,
        string model,
        int index,
        int totalCount,
        FoundryCatalogSnapshot snapshot,
        Action<ModelBenchmarkProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!ShouldRegisterFoundryAcceleration(snapshot.Acceleration))
        {
            return snapshot;
        }

        progress?.Invoke(new ModelBenchmarkProgress(
            Model: model,
            Phase: ModelBenchmarkRunPhase.Preparing,
            Detail: "Registering Windows ML execution providers before benchmark.",
            CompletedFixtureCount: 0,
            TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count));

        try
        {
            var preferredExecutionProviders = GetPreferredExecutionProviders();
            var acceleration = await catalogClient.RegisterExecutionProvidersAsync(
                preferredExecutionProviders.Count == 0 ? null : preferredExecutionProviders,
                (providerName, registrationPercent) => progress?.Invoke(new ModelBenchmarkProgress(
                    Model: model,
                    Phase: ModelBenchmarkRunPhase.Preparing,
                    Detail: string.IsNullOrWhiteSpace(providerName)
                        ? $"Registering Windows ML execution providers: {registrationPercent:0.0}%"
                        : $"Registering Windows ML provider '{providerName}': {registrationPercent:0.0}%",
                    CompletedFixtureCount: 0,
                    TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count)),
                cancellationToken);

            return snapshot with
            {
                Acceleration = acceleration,
                CollectedAtUtc = timeProvider.GetUtcNow()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            operations.Error($"Could not register Windows ML execution providers before benchmarking {model}.", exception.Message);
            return snapshot with
            {
                Acceleration = FoundryAccelerationSnapshot.Unavailable($"Execution-provider registration failed before benchmark: {exception.Message}"),
                CollectedAtUtc = timeProvider.GetUtcNow()
            };
        }
    }

    private async Task RemoveFoundryModelAsync(IServiceProvider serviceProvider, string model, CancellationToken cancellationToken)
    {
        operations.UpdateCurrent($"Removing {model}", "Removing non-fitting model from the local Foundry cache…");
        var catalogClient = serviceProvider.GetRequiredService<IFoundryCatalogClient>();
        await catalogClient.RemoveModelAsync(model, cancellationToken);
    }

    private async Task<ModelBenchmarkResult> FinalizeFoundryBenchmarkResultAsync(
        IServiceProvider serviceProvider,
        string model,
        ModelBenchmarkResult result,
        bool removeTooLargeModelsAfterBenchmark,
        CancellationToken cancellationToken)
    {
        var finalizedResult = result;

        if (removeTooLargeModelsAfterBenchmark && ShouldRemoveFoundryModelAfterBenchmark(result))
        {
            try
            {
                await RemoveFoundryModelAsync(serviceProvider, model, cancellationToken);
                finalizedResult = AppendBenchmarkNote(
                    finalizedResult,
                    "Removed from the local Foundry cache after benchmark because it was classified as too large for interactive use.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                operations.Error($"Could not remove Foundry model {model} after benchmark.", exception.Message);
                finalizedResult = AppendBenchmarkNote(
                    finalizedResult,
                    $"Benchmark completed, but the model could not be removed from the local Foundry cache: {exception.Message}");
            }
        }

        try
        {
            await RefreshFoundryAvailabilityAsync(serviceProvider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            operations.Error("Could not refresh Foundry catalog after benchmark.", exception.Message);
            finalizedResult = AppendBenchmarkNote(
                finalizedResult,
                $"Benchmark completed, but the Foundry catalog refresh failed afterward: {exception.Message}");
        }

        return finalizedResult;
    }

    private async Task RefreshFoundryAvailabilityAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var catalogClient = serviceProvider.GetRequiredService<IFoundryCatalogClient>();
        var snapshot = await catalogClient.GetSnapshotAsync(cancellationToken);
        workspace.SetFoundryAvailability(snapshot.Availability);
    }

    private static ModelBenchmarkResult AppendBenchmarkNote(ModelBenchmarkResult result, string note)
        => string.IsNullOrWhiteSpace(note)
            ? result
            : result with { Notes = [.. result.Notes, note] };

    private static ModelBenchmarkResult ApplyFoundryAccelerationNotes(ModelBenchmarkResult result, FoundryAccelerationSnapshot acceleration)
        => acceleration.Readiness == FoundryAccelerationReadiness.Ready
            ? result
            : AppendBenchmarkNote(result, $"Foundry acceleration during benchmark: {acceleration.GuidanceMessage}");

    private static bool ShouldRegisterFoundryAcceleration(FoundryAccelerationSnapshot acceleration)
        => acceleration.Readiness is FoundryAccelerationReadiness.NeedsRegistration or FoundryAccelerationReadiness.PartiallyReady;

    private IReadOnlyList<string> GetPreferredExecutionProviders()
        => foundryOptions.PreferredExecutionProviders
            .Where(static providerName => !string.IsNullOrWhiteSpace(providerName))
            .Select(static providerName => providerName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool ShouldRemoveFoundryModelAfterBenchmark(ModelBenchmarkResult result)
        => result.Fit == OllamaCapacityFit.TooLargeForInteractive;

    private static IReadOnlyList<ModelBenchmarkResult> RankResults(IEnumerable<ModelBenchmarkResult> results)
    {
        var ordered = results
            .OrderByDescending(static result => result.Succeeded)
            .ThenByDescending(static result => result.OverallScore)
            .ThenByDescending(static result => result.QualityScore)
            .ToArray();

        for (var index = 0; index < ordered.Length; index++)
        {
            ordered[index] = ordered[index] with { Rank = index + 1 };
        }

        return ordered;
    }
}
