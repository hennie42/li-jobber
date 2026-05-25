using System.Diagnostics;
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
    TimeProvider timeProvider,
    ModelBenchmarkHangClockPolicy hangClockPolicy)
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
        var startedAt = timeProvider.GetUtcNow();
        lock (gate)
        {
            if (current is { IsRunning: true })
            {
                throw new InvalidOperationException("A benchmark run is already in progress.");
            }

            activeCts = new CancellationTokenSource();
            token = activeCts.Token;
            current = new ModelBenchmarkSession(
                StartedUtc: startedAt,
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
                TotalFixtureCount = ModelBenchmarkFixtures.DefaultSuite.Count,
                UpdatedUtc = startedAt,
                LastRealProgressUtc = startedAt,
                Diagnostics = ModelBenchmarkDiagnostics.Empty,
                CurrentPhaseStartedUtc = startedAt
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
            var hangClock = new ModelHangClockState(hangClockPolicy, timeProvider.GetUtcNow());
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
                    TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count),
                hangClock);

            ModelBenchmarkResult result;
            var modelDiagnostics = ModelBenchmarkDiagnostics.Empty;
            IReadOnlyList<string> foundryPreparationNotes = Array.Empty<string>();
            using var hangTerminationCts = new CancellationTokenSource();
            using var modelLifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, hangTerminationCts.Token);
            using var monitorStopCts = new CancellationTokenSource();
            var modelLifetimeToken = modelLifetimeCts.Token;
            var hangMonitorTask = MonitorModelHangAsync(
                model,
                results.ToArray(),
                hangClock,
                hangTerminationCts,
                monitorStopCts.Token);

            try
            {
                using var scope = scopeFactory.CreateScope();
                FoundryAccelerationSnapshot? foundryAcceleration = null;
                if (provider == LlmProviderKind.Foundry)
                {
                    var preparationStopwatch = Stopwatch.StartNew();
                    var foundryPreparation = await EnsureFoundryModelReadyAsync(
                        scope.ServiceProvider,
                        model,
                        index,
                        models.Count,
                        downloadMissingModels,
                        progress => ReportLiveProgress(model, index, models.Count, results.ToArray(), progress, hangClock),
                        modelLifetimeToken);
                    preparationStopwatch.Stop();
                    foundryAcceleration = foundryPreparation.Snapshot.Acceleration;
                    foundryPreparationNotes = foundryPreparation.Notes;
                    modelDiagnostics = MergeDiagnostics(
                        modelDiagnostics,
                        CreateFoundryPreparationDiagnostics(foundryAcceleration, preparationStopwatch.Elapsed));
                }

                var benchmarkService = ResolveBenchmarkService(scope.ServiceProvider, provider);
                using var perModelTimeoutCts = perModelTimeoutSeconds > 0
                    ? new CancellationTokenSource(TimeSpan.FromSeconds(perModelTimeoutSeconds))
                    : null;
                using var benchmarkWorkCts = perModelTimeoutCts is null
                    ? CancellationTokenSource.CreateLinkedTokenSource(modelLifetimeToken)
                    : CancellationTokenSource.CreateLinkedTokenSource(modelLifetimeToken, perModelTimeoutCts.Token);
                result = await benchmarkService.RunSingleAsync(
                    model,
                    progress => ReportLiveProgress(model, index, models.Count, results.ToArray(), progress, hangClock),
                    benchmarkWorkCts.Token);
                result = result with
                {
                    Provider = provider,
                    Diagnostics = MergeDiagnostics(modelDiagnostics, result.Diagnostics)
                };
                result = AppendBenchmarkNotes(result, foundryPreparationNotes);

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
                            TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count,
                            Diagnostics: result.Diagnostics),
                        hangClock);

                    var cleanupStopwatch = Stopwatch.StartNew();
                    result = await FinalizeFoundryBenchmarkResultAsync(
                        scope.ServiceProvider,
                        model,
                        result,
                        removeTooLargeModelsAfterBenchmark,
                        modelLifetimeToken);
                    cleanupStopwatch.Stop();
                    result = result with
                    {
                        Diagnostics = MergeDiagnostics(result.Diagnostics, ModelBenchmarkDiagnostics.Empty with { CleanupDuration = cleanupStopwatch.Elapsed })
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            catch (OperationCanceledException) when (hangTerminationCts.IsCancellationRequested)
            {
                result = CreateHungResult(model, provider, hangClock, timeProvider.GetUtcNow());
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
                    FixtureResults: Array.Empty<ModelBenchmarkFixtureResult>())
                {
                    Diagnostics = modelDiagnostics
                };
                result = provider == LlmProviderKind.Foundry
                    ? AppendBenchmarkNotes(result, foundryPreparationNotes)
                    : result;
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
                    FixtureResults: Array.Empty<ModelBenchmarkFixtureResult>())
                {
                    Diagnostics = modelDiagnostics
                };
                result = provider == LlmProviderKind.Foundry
                    ? AppendBenchmarkNotes(result, foundryPreparationNotes)
                    : result;
            }
            finally
            {
                monitorStopCts.Cancel();
                await ObserveHangMonitorAsync(hangMonitorTask);
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
                    TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count),
                hangClock);
        }

        var ranked = RankResults(results);
        var completedAt = timeProvider.GetUtcNow();
        var session = new ModelBenchmarkSession(
            StartedUtc: current?.StartedUtc ?? timeProvider.GetUtcNow(),
            CompletedUtc: completedAt,
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
            TotalFixtureCount = ModelBenchmarkFixtures.DefaultSuite.Count,
            UpdatedUtc = completedAt,
            LastRealProgressUtc = current?.LastRealProgressUtc ?? completedAt,
            HangState = ModelBenchmarkHangState.None,
            HangDetail = null,
            HangWarningStartedUtc = null,
            HangDeadlineUtc = null,
            Diagnostics = current?.Diagnostics ?? ModelBenchmarkDiagnostics.Empty,
            CurrentPhaseStartedUtc = current?.CurrentPhaseStartedUtc ?? completedAt
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

    private void UpdateProgress(
        string? currentModel,
        IReadOnlyList<ModelBenchmarkResult> partialResults,
        int completedCount,
        ModelBenchmarkProgress progress,
        ModelHangClockState hangClock,
        DateTimeOffset updatedAt)
    {
        var rankedPartialResults = RankResults(partialResults);

        lock (gate)
        {
            if (current is null)
            {
                return;
            }

            var mergedDiagnostics = MergeDiagnostics(current.Diagnostics, progress.Diagnostics);
            var phaseStartedUtc = current.CurrentPhase == progress.Phase
                && string.Equals(current.CurrentModel, currentModel, StringComparison.Ordinal)
                ? current.CurrentPhaseStartedUtc ?? updatedAt
                : updatedAt;

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
                TotalFixtureCount = progress.TotalFixtureCount,
                UpdatedUtc = updatedAt,
                LastRealProgressUtc = hangClock.LastRealProgressUtc,
                HangState = hangClock.HangState,
                HangDetail = hangClock.HangDetail,
                HangWarningStartedUtc = hangClock.WarningStartedUtc,
                HangDeadlineUtc = hangClock.DeadlineUtc,
                Diagnostics = mergedDiagnostics,
                CurrentPhaseStartedUtc = phaseStartedUtc
            };
        }

        Changed?.Invoke();
    }

    private void ReportLiveProgress(
        string? currentModel,
        int completedCount,
        int totalCount,
        IReadOnlyList<ModelBenchmarkResult> partialResults,
        ModelBenchmarkProgress progress,
        ModelHangClockState hangClock)
    {
        var updatedAt = timeProvider.GetUtcNow();
        var message = GetProgressMessage(currentModel ?? progress.Model, completedCount, totalCount, progress);
        hangClock.RecordRealProgress(progress, updatedAt);
        UpdateProgress(currentModel, partialResults, completedCount, progress, hangClock, updatedAt);

        if (progress.LlmTelemetry is { } llmTelemetry)
        {
            operations.UpdateCurrent(llmTelemetry with
            {
                Message = message,
                Detail = progress.Detail,
                Model = progress.Model
            });
            return;
        }

        operations.UpdateCurrent(message, progress.Detail);
    }

    /// <summary>
    /// Monitors the active model slot for missing benchmark progress and escalates from warning to cancellation.
    /// </summary>
    private async Task MonitorModelHangAsync(
        string model,
        IReadOnlyList<ModelBenchmarkResult> partialResults,
        ModelHangClockState hangClock,
        CancellationTokenSource hangTerminationCts,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(hangClockPolicy.PollInterval, timeProvider);

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var now = timeProvider.GetUtcNow();
            if (hangClock.TryEnterWarning(now))
            {
                UpdateHangState(model, partialResults, hangClock, now);
                operations.Info($"Monitoring suspected benchmark hang: {model}", hangClock.HangDetail);
            }

            if (!hangClock.ShouldTerminate(now))
            {
                continue;
            }

            UpdateHangState(model, partialResults, hangClock, now);
            hangTerminationCts.Cancel();
            break;
        }
    }

    /// <summary>
    /// Updates the live session with warning-state information without counting it as worker progress.
    /// </summary>
    private void UpdateHangState(
        string currentModel,
        IReadOnlyList<ModelBenchmarkResult> partialResults,
        ModelHangClockState hangClock,
        DateTimeOffset updatedAt)
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
                Results = rankedPartialResults,
                UpdatedUtc = updatedAt,
                LastRealProgressUtc = hangClock.LastRealProgressUtc,
                HangState = hangClock.HangState,
                HangDetail = hangClock.HangDetail,
                HangWarningStartedUtc = hangClock.WarningStartedUtc,
                HangDeadlineUtc = hangClock.DeadlineUtc
            };
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Observes the background hang monitor so cancellation does not leak a fault into the main benchmark flow.
    /// </summary>
    private static async Task ObserveHangMonitorAsync(Task hangMonitorTask)
    {
        try
        {
            await hangMonitorTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Creates a failed benchmark row for a model that stopped reporting real progress during the grace window.
    /// </summary>
    private static ModelBenchmarkResult CreateHungResult(
        string model,
        LlmProviderKind provider,
        ModelHangClockState hangClock,
        DateTimeOffset now)
        => new(
            Model: model,
            Rank: 0,
            OverallScore: 0.0,
            QualityScore: 0.0,
            DecodeTokensPerSecond: null,
            LoadDuration: null,
            TotalDuration: null,
            Fit: OllamaCapacityFit.Unknown,
            Notes: Array.Empty<string>(),
            FailedReason: hangClock.BuildFailureReason(now),
            Provider: provider,
            FixtureResults: Array.Empty<ModelBenchmarkFixtureResult>())
        {
            Diagnostics = ModelBenchmarkDiagnostics.Empty
        };

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

    /// <summary>
    /// Prepares a Foundry benchmark model from a clean runtime snapshot so earlier runs do not contaminate the next score.
    /// </summary>
    private async Task<FoundryPreparationResult> EnsureFoundryModelReadyAsync(
        IServiceProvider serviceProvider,
        string model,
        int index,
        int totalCount,
        bool downloadMissingModels,
        Action<ModelBenchmarkProgress>? progress,
        CancellationToken cancellationToken)
    {
        var catalogClient = serviceProvider.GetRequiredService<IFoundryCatalogClient>();
        var snapshot = await RefreshFoundrySnapshotAsync(catalogClient, cancellationToken);
        var isolation = await EnsureFoundryBenchmarkIsolationAsync(catalogClient, model, snapshot, progress, cancellationToken);
        snapshot = isolation.Snapshot;
        var currentDiagnostics = CreateFoundryPreparationDiagnostics(snapshot.Acceleration);
        snapshot = await EnsureFoundryAccelerationReadyAsync(catalogClient, model, index, totalCount, snapshot, progress, cancellationToken);
        currentDiagnostics = CreateFoundryPreparationDiagnostics(snapshot.Acceleration);

        if (snapshot.Availability.AvailableModels.Any(alias => alias.Equals(model, StringComparison.OrdinalIgnoreCase)))
        {
            progress?.Invoke(new ModelBenchmarkProgress(
                Model: model,
                Phase: ModelBenchmarkRunPhase.Preparing,
                Detail: "Model is already cached locally; benchmark warm-up will start next.",
                CompletedFixtureCount: 0,
                TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count,
                Diagnostics: currentDiagnostics));
            return new FoundryPreparationResult(snapshot, isolation.Notes);
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
            TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count,
            Diagnostics: currentDiagnostics));

        await catalogClient.DownloadModelAsync(
            model,
            downloadPercent => progress?.Invoke(new ModelBenchmarkProgress(
                Model: model,
                Phase: ModelBenchmarkRunPhase.Preparing,
                Detail: FoundryProgressFormatter.FormatDetail("Downloading model before benchmark", downloadPercent),
                CompletedFixtureCount: 0,
                TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count,
                Diagnostics: currentDiagnostics)),
            cancellationToken);

        var refreshedSnapshot = await RefreshFoundrySnapshotAsync(catalogClient, cancellationToken);
        return new FoundryPreparationResult(refreshedSnapshot, isolation.Notes);
    }

    /// <summary>
    /// Unloads any models that the Foundry runtime still reports as loaded before the next benchmark starts.
    /// </summary>
    private async Task<FoundryIsolationResult> EnsureFoundryBenchmarkIsolationAsync(
        IFoundryCatalogClient catalogClient,
        string currentModel,
        FoundryCatalogSnapshot snapshot,
        Action<ModelBenchmarkProgress>? progress,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        snapshot = await UnloadFoundryAliasesAsync(
            catalogClient,
            currentModel,
            snapshot,
            GetLoadedFoundryAliases(snapshot),
            progress,
            notes,
            isRetry: false,
            cancellationToken);

        var remainingAliases = GetLoadedFoundryAliases(snapshot);
        if (remainingAliases.Count == 0)
        {
            return new FoundryIsolationResult(snapshot, notes);
        }

        progress?.Invoke(new ModelBenchmarkProgress(
            Model: currentModel,
            Phase: ModelBenchmarkRunPhase.Preparing,
            Detail: "Foundry still reports loaded models after cleanup; retrying targeted unload before benchmark.",
            CompletedFixtureCount: 0,
            TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count,
            Diagnostics: CreateFoundryPreparationDiagnostics(snapshot.Acceleration)));

        snapshot = await UnloadFoundryAliasesAsync(
            catalogClient,
            currentModel,
            snapshot,
            remainingAliases,
            progress,
            notes,
            isRetry: true,
            cancellationToken);

        remainingAliases = GetLoadedFoundryAliases(snapshot);
        if (remainingAliases.Count > 0)
        {
            notes.Add($"Foundry still reported loaded models before benchmarking {currentModel}: {string.Join(", ", remainingAliases)}. Benchmarking continued without an engine reset.");
        }

        return new FoundryIsolationResult(snapshot, notes);
    }

    /// <summary>
    /// Attempts to unload the provided aliases and records non-fatal isolation issues as benchmark notes.
    /// </summary>
    private async Task<FoundryCatalogSnapshot> UnloadFoundryAliasesAsync(
        IFoundryCatalogClient catalogClient,
        string currentModel,
        FoundryCatalogSnapshot snapshot,
        IReadOnlyList<string> aliases,
        Action<ModelBenchmarkProgress>? progress,
        ICollection<string> notes,
        bool isRetry,
        CancellationToken cancellationToken)
    {
        if (aliases.Count == 0)
        {
            return snapshot;
        }

        foreach (var alias in aliases)
        {
            progress?.Invoke(new ModelBenchmarkProgress(
                Model: currentModel,
                Phase: ModelBenchmarkRunPhase.Preparing,
                Detail: isRetry
                    ? $"Retrying unload for previously loaded Foundry model '{alias}' before benchmark."
                    : $"Unloading previously loaded Foundry model '{alias}' before benchmark.",
                CompletedFixtureCount: 0,
                TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count,
                Diagnostics: CreateFoundryPreparationDiagnostics(snapshot.Acceleration)));

            try
            {
                await catalogClient.UnloadModelAsync(alias, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                operations.Error($"Could not unload previously loaded Foundry model {alias} before benchmarking {currentModel}.", exception.Message);
                notes.Add($"Foundry benchmark isolation could not unload previously loaded model '{alias}' before benchmarking {currentModel}: {exception.Message}");
            }
        }

        return await RefreshFoundrySnapshotAsync(catalogClient, cancellationToken);
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
            TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count,
            Diagnostics: CreateFoundryPreparationDiagnostics(snapshot.Acceleration)));

        try
        {
            var preferredExecutionProviders = GetPreferredExecutionProviders();
            var acceleration = await catalogClient.RegisterExecutionProvidersAsync(
                preferredExecutionProviders.Count == 0 ? null : preferredExecutionProviders,
                (providerName, registrationPercent) => progress?.Invoke(new ModelBenchmarkProgress(
                    Model: model,
                    Phase: ModelBenchmarkRunPhase.Preparing,
                    Detail: FoundryProgressFormatter.FormatDetail(
                        string.IsNullOrWhiteSpace(providerName)
                            ? "Registering Windows ML execution providers"
                            : $"Registering Windows ML provider '{providerName}'",
                        registrationPercent),
                    CompletedFixtureCount: 0,
                    TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count,
                    Diagnostics: CreateFoundryPreparationDiagnostics(snapshot.Acceleration))),
                cancellationToken);

            progress?.Invoke(new ModelBenchmarkProgress(
                Model: model,
                Phase: ModelBenchmarkRunPhase.Preparing,
                Detail: "Windows ML execution providers are ready for Foundry benchmark preparation.",
                CompletedFixtureCount: 0,
                TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count,
                Diagnostics: CreateFoundryPreparationDiagnostics(acceleration)));

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

    private async Task UnloadFoundryModelAsync(IServiceProvider serviceProvider, string model, CancellationToken cancellationToken)
    {
        operations.UpdateCurrent($"Unloading {model}", "Releasing the benchmarked Foundry model from the active runtime…");
        var catalogClient = serviceProvider.GetRequiredService<IFoundryCatalogClient>();
        await catalogClient.UnloadModelAsync(model, cancellationToken);
    }

    private async Task<ModelBenchmarkResult> FinalizeFoundryBenchmarkResultAsync(
        IServiceProvider serviceProvider,
        string model,
        ModelBenchmarkResult result,
        bool removeTooLargeModelsAfterBenchmark,
        CancellationToken cancellationToken)
    {
        var finalizedResult = result;
        var removedFromCache = false;

        if (removeTooLargeModelsAfterBenchmark && ShouldRemoveFoundryModelAfterBenchmark(result))
        {
            try
            {
                await RemoveFoundryModelAsync(serviceProvider, model, cancellationToken);
                removedFromCache = true;
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

        if (!removedFromCache)
        {
            try
            {
                await UnloadFoundryModelAsync(serviceProvider, model, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                operations.Error($"Could not unload Foundry model {model} after benchmark.", exception.Message);
                finalizedResult = AppendBenchmarkNote(
                    finalizedResult,
                    $"Benchmark completed, but the model could not be unloaded from the active Foundry runtime: {exception.Message}");
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
        var snapshot = await RefreshFoundrySnapshotAsync(catalogClient, cancellationToken);
        workspace.SetFoundryAvailability(snapshot.Availability);
    }

    private async Task<FoundryCatalogSnapshot> RefreshFoundrySnapshotAsync(
        IFoundryCatalogClient catalogClient,
        CancellationToken cancellationToken)
    {
        var snapshot = await catalogClient.GetSnapshotAsync(cancellationToken);
        workspace.SetFoundryAvailability(snapshot.Availability);
        return snapshot;
    }

    private static ModelBenchmarkResult AppendBenchmarkNote(ModelBenchmarkResult result, string note)
        => string.IsNullOrWhiteSpace(note)
            ? result
            : result with { Notes = [.. result.Notes, note] };

    private static ModelBenchmarkResult AppendBenchmarkNotes(ModelBenchmarkResult result, IEnumerable<string> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);

        foreach (var note in notes)
        {
            result = AppendBenchmarkNote(result, note);
        }

        return result;
    }

    private static IReadOnlyList<string> GetLoadedFoundryAliases(FoundryCatalogSnapshot snapshot)
        => snapshot.Models
            .Where(static model => model.IsLoaded)
            .Select(static model => model.Alias)
            .Concat((snapshot.Availability.RunningModels ?? Array.Empty<LlmRunningModel>()).Select(static model => model.Name))
            .Where(static alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static ModelBenchmarkResult ApplyFoundryAccelerationNotes(ModelBenchmarkResult result, FoundryAccelerationSnapshot acceleration)
        => (acceleration.Readiness == FoundryAccelerationReadiness.Ready
            ? result
            : AppendBenchmarkNote(result, $"Foundry acceleration during benchmark: {acceleration.GuidanceMessage}")) with
        {
            Diagnostics = MergeDiagnostics(
                result.Diagnostics,
                CreateFoundryPreparationDiagnostics(acceleration))
        };

    private static ModelBenchmarkDiagnostics CreateFoundryPreparationDiagnostics(
        FoundryAccelerationSnapshot acceleration,
        TimeSpan? preparationDuration = null)
        => ModelBenchmarkDiagnostics.Empty with
        {
            PreparationDuration = preparationDuration,
            AccelerationReadiness = acceleration.Readiness.ToString(),
            AccelerationStatusMessage = acceleration.StatusMessage,
            RuntimePathSummary = BuildFoundryRuntimePathSummary(acceleration)
        };

    private static string BuildFoundryRuntimePathSummary(FoundryAccelerationSnapshot acceleration)
    {
        if (!acceleration.IsSupported)
        {
            return "Foundry Local did not expose Windows ML acceleration details for this runtime.";
        }

        var registeredProviders = acceleration.ExecutionProviders
            .Where(static provider => provider.IsRegistered)
            .Select(static provider => provider.DisplayName)
            .ToArray();

        if (registeredProviders.Length > 0)
        {
            return $"Registered execution providers: {string.Join(", ", registeredProviders)}.";
        }

        return acceleration.ExecutionProviders.Count > 0
            ? "Execution providers were discovered, but none are confirmed registered yet."
            : "Foundry did not report any execution providers for this runtime.";
    }

    private static ModelBenchmarkDiagnostics MergeDiagnostics(
        ModelBenchmarkDiagnostics? baseline,
        ModelBenchmarkDiagnostics? incoming)
    {
        var current = baseline ?? ModelBenchmarkDiagnostics.Empty;
        if (incoming is null)
        {
            return current;
        }

        return current with
        {
            PreparationDuration = incoming.PreparationDuration ?? current.PreparationDuration,
            WarmupDuration = incoming.WarmupDuration ?? current.WarmupDuration,
            EvaluationDuration = incoming.EvaluationDuration ?? current.EvaluationDuration,
            CleanupDuration = incoming.CleanupDuration ?? current.CleanupDuration,
            FinalizationDuration = incoming.FinalizationDuration ?? current.FinalizationDuration,
            AccelerationReadiness = incoming.AccelerationReadiness ?? current.AccelerationReadiness,
            AccelerationStatusMessage = incoming.AccelerationStatusMessage ?? current.AccelerationStatusMessage,
            RuntimePathSummary = incoming.RuntimePathSummary ?? current.RuntimePathSummary
        };
    }

    private static bool ShouldRegisterFoundryAcceleration(FoundryAccelerationSnapshot acceleration)
        => acceleration.Readiness is FoundryAccelerationReadiness.NeedsRegistration or FoundryAccelerationReadiness.PartiallyReady;

    private sealed record FoundryPreparationResult(FoundryCatalogSnapshot Snapshot, IReadOnlyList<string> Notes);

    private sealed record FoundryIsolationResult(FoundryCatalogSnapshot Snapshot, IReadOnlyList<string> Notes);

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

    private sealed class ModelHangClockState
    {
        private readonly object gate = new();
        private readonly ModelBenchmarkHangClockPolicy policy;
        private BenchmarkProgressSignature? lastSignature;
        private ModelBenchmarkRunPhase currentPhase;
        private string? currentFixtureDisplayName;
        private int currentFixtureNumber;

        public ModelHangClockState(ModelBenchmarkHangClockPolicy policy, DateTimeOffset startedAt)
        {
            this.policy = policy;
            LastRealProgressUtc = startedAt;
        }

        public DateTimeOffset LastRealProgressUtc { get; private set; }

        public ModelBenchmarkHangState HangState { get; private set; }

        public string? HangDetail { get; private set; }

        public DateTimeOffset? WarningStartedUtc { get; private set; }

        public DateTimeOffset? DeadlineUtc { get; private set; }

        public void RecordRealProgress(ModelBenchmarkProgress progress, DateTimeOffset now)
        {
            lock (gate)
            {
                var nextSignature = new BenchmarkProgressSignature(
                    progress.Phase,
                    progress.CompletedFixtureCount,
                    progress.CurrentFixtureNumber,
                    progress.CurrentFixtureId,
                    progress.Detail);

                if (lastSignature == nextSignature)
                {
                    return;
                }

                lastSignature = nextSignature;
                currentPhase = progress.Phase;
                currentFixtureDisplayName = progress.CurrentFixtureDisplayName;
                currentFixtureNumber = progress.CurrentFixtureNumber;
                LastRealProgressUtc = now;
                HangState = ModelBenchmarkHangState.None;
                HangDetail = null;
                WarningStartedUtc = null;
                DeadlineUtc = null;
            }
        }

        public bool TryEnterWarning(DateTimeOffset now)
        {
            lock (gate)
            {
                if (HangState == ModelBenchmarkHangState.Warning || (now - LastRealProgressUtc) < policy.WarningAfter)
                {
                    return false;
                }

                HangState = ModelBenchmarkHangState.Warning;
                WarningStartedUtc = now;
                DeadlineUtc = now + policy.GracePeriod;
                HangDetail = BuildHangDetail(now, prefix: "No benchmark progress detected");
                return true;
            }
        }

        public bool ShouldTerminate(DateTimeOffset now)
        {
            lock (gate)
            {
                return HangState == ModelBenchmarkHangState.Warning
                    && DeadlineUtc is { } deadline
                    && now >= deadline;
            }
        }

        public string BuildFailureReason(DateTimeOffset now)
        {
            lock (gate)
            {
                return BuildHangDetail(now, prefix: "Benchmark hang detected");
            }
        }

        private string BuildHangDetail(DateTimeOffset now, string prefix)
        {
            var phaseLabel = currentPhase switch
            {
                ModelBenchmarkRunPhase.Preparing => "while preparing the current model",
                ModelBenchmarkRunPhase.Warmup => "during warm-up",
                ModelBenchmarkRunPhase.Evaluating => currentFixtureNumber > 0 && !string.IsNullOrWhiteSpace(currentFixtureDisplayName)
                    ? $"during fixture {currentFixtureNumber}: {currentFixtureDisplayName}"
                    : "during evaluation",
                ModelBenchmarkRunPhase.Cleanup => "during cleanup",
                ModelBenchmarkRunPhase.Finalizing => "during finalization",
                _ => "during the current benchmark phase"
            };

            return $"{prefix} for {FormatInactivity(now - LastRealProgressUtc)} {phaseLabel}. If the stall continues, the queue will move on.";
        }

        private static string FormatInactivity(TimeSpan inactivity)
            => inactivity.TotalMinutes >= 1
                ? $"{Math.Ceiling(inactivity.TotalMinutes):0} minute(s)"
                : $"{Math.Ceiling(Math.Max(inactivity.TotalSeconds, 1)):0} second(s)";
    }

    private sealed record BenchmarkProgressSignature(
        ModelBenchmarkRunPhase Phase,
        int CompletedFixtureCount,
        int CurrentFixtureNumber,
        string? CurrentFixtureId,
        string Detail);
}
