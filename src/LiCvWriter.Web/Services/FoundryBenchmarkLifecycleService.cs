using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LiCvWriter.Web.Services;

/// <summary>
/// Handles Foundry-specific benchmark preparation and cleanup outside the queue coordinator.
/// </summary>
public sealed class FoundryBenchmarkLifecycleService(
    WorkspaceSession workspace,
    OperationStatusService operations,
    FoundryOptions foundryOptions,
    TimeProvider timeProvider)
{
    public async Task<FoundryBenchmarkPreparationResult> PrepareAsync(
        IServiceProvider serviceProvider,
        string model,
        bool downloadMissingModels,
        Action<ModelBenchmarkProgress>? progress,
        CancellationToken cancellationToken)
    {
        var catalogClient = serviceProvider.GetRequiredService<IFoundryCatalogClient>();
        var snapshot = await RefreshFoundrySnapshotAsync(catalogClient, cancellationToken);
        var isolation = await EnsureBenchmarkIsolationAsync(catalogClient, model, snapshot, progress, cancellationToken);
        snapshot = isolation.Snapshot;
        var currentDiagnostics = CreatePreparationDiagnostics(snapshot.Acceleration);
        snapshot = await EnsureAccelerationReadyAsync(catalogClient, model, snapshot, progress, cancellationToken);
        currentDiagnostics = CreatePreparationDiagnostics(snapshot.Acceleration);

        if (snapshot.Availability.AvailableModels.Any(alias => alias.Equals(model, StringComparison.OrdinalIgnoreCase)))
        {
            progress?.Invoke(new ModelBenchmarkProgress(
                Model: model,
                Phase: ModelBenchmarkRunPhase.Preparing,
                Detail: "Model is already cached locally; benchmark warm-up will start next.",
                CompletedFixtureCount: 0,
                TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count,
                Diagnostics: currentDiagnostics));
            return new FoundryBenchmarkPreparationResult(snapshot, isolation.Notes);
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
        return new FoundryBenchmarkPreparationResult(refreshedSnapshot, isolation.Notes);
    }

    public async Task<ModelBenchmarkResult> FinalizeAsync(
        IServiceProvider serviceProvider,
        string model,
        ModelBenchmarkResult result,
        bool removeTooLargeModelsAfterBenchmark,
        CancellationToken cancellationToken)
    {
        var finalizedResult = result;
        var removedFromCache = false;

        if (removeTooLargeModelsAfterBenchmark && ShouldRemoveModelAfterBenchmark(result))
        {
            try
            {
                await RemoveModelAsync(serviceProvider, model, cancellationToken);
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
                await UnloadModelAsync(serviceProvider, model, cancellationToken);
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
            await RefreshAvailabilityAsync(serviceProvider, cancellationToken);
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

    public static ModelBenchmarkResult ApplyAccelerationNotes(ModelBenchmarkResult result, FoundryAccelerationSnapshot acceleration)
        => (acceleration.Readiness == FoundryAccelerationReadiness.Ready
            ? result
            : AppendBenchmarkNote(result, $"Foundry acceleration during benchmark: {acceleration.GuidanceMessage}")) with
        {
            Diagnostics = ModelBenchmarkCoordinator.MergeDiagnostics(
                result.Diagnostics,
                CreatePreparationDiagnostics(acceleration))
        };

    public static ModelBenchmarkDiagnostics CreatePreparationDiagnostics(
        FoundryAccelerationSnapshot acceleration,
        TimeSpan? preparationDuration = null)
        => ModelBenchmarkDiagnostics.Empty with
        {
            PreparationDuration = preparationDuration,
            AccelerationReadiness = acceleration.Readiness.ToString(),
            AccelerationStatusMessage = acceleration.StatusMessage,
            RuntimePathSummary = BuildRuntimePathSummary(acceleration)
        };

    private async Task<FoundryBenchmarkIsolationResult> EnsureBenchmarkIsolationAsync(
        IFoundryCatalogClient catalogClient,
        string currentModel,
        FoundryCatalogSnapshot snapshot,
        Action<ModelBenchmarkProgress>? progress,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        snapshot = await UnloadAliasesAsync(
            catalogClient,
            currentModel,
            snapshot,
            GetLoadedAliases(snapshot),
            progress,
            notes,
            isRetry: false,
            cancellationToken);

        var remainingAliases = GetLoadedAliases(snapshot);
        if (remainingAliases.Count == 0)
        {
            return new FoundryBenchmarkIsolationResult(snapshot, notes);
        }

        progress?.Invoke(new ModelBenchmarkProgress(
            Model: currentModel,
            Phase: ModelBenchmarkRunPhase.Preparing,
            Detail: "Foundry still reports loaded models after cleanup; retrying targeted unload before benchmark.",
            CompletedFixtureCount: 0,
            TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count,
            Diagnostics: CreatePreparationDiagnostics(snapshot.Acceleration)));

        snapshot = await UnloadAliasesAsync(
            catalogClient,
            currentModel,
            snapshot,
            remainingAliases,
            progress,
            notes,
            isRetry: true,
            cancellationToken);

        remainingAliases = GetLoadedAliases(snapshot);
        if (remainingAliases.Count > 0)
        {
            notes.Add($"Foundry still reported loaded models before benchmarking {currentModel}: {string.Join(", ", remainingAliases)}. Benchmarking continued without an engine reset.");
        }

        return new FoundryBenchmarkIsolationResult(snapshot, notes);
    }

    private async Task<FoundryCatalogSnapshot> UnloadAliasesAsync(
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
                Diagnostics: CreatePreparationDiagnostics(snapshot.Acceleration)));

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

    private async Task<FoundryCatalogSnapshot> EnsureAccelerationReadyAsync(
        IFoundryCatalogClient catalogClient,
        string model,
        FoundryCatalogSnapshot snapshot,
        Action<ModelBenchmarkProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!ShouldRegisterAcceleration(snapshot.Acceleration))
        {
            return snapshot;
        }

        progress?.Invoke(new ModelBenchmarkProgress(
            Model: model,
            Phase: ModelBenchmarkRunPhase.Preparing,
            Detail: "Registering Windows ML execution providers before benchmark.",
            CompletedFixtureCount: 0,
            TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count,
            Diagnostics: CreatePreparationDiagnostics(snapshot.Acceleration)));

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
                    Diagnostics: CreatePreparationDiagnostics(snapshot.Acceleration))),
                cancellationToken);

            progress?.Invoke(new ModelBenchmarkProgress(
                Model: model,
                Phase: ModelBenchmarkRunPhase.Preparing,
                Detail: "Windows ML execution providers are ready for Foundry benchmark preparation.",
                CompletedFixtureCount: 0,
                TotalFixtureCount: ModelBenchmarkFixtures.DefaultSuite.Count,
                Diagnostics: CreatePreparationDiagnostics(acceleration)));

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

    private async Task RemoveModelAsync(IServiceProvider serviceProvider, string model, CancellationToken cancellationToken)
    {
        operations.UpdateCurrent($"Removing {model}", "Removing non-fitting model from the local Foundry cache…");
        var catalogClient = serviceProvider.GetRequiredService<IFoundryCatalogClient>();
        await catalogClient.RemoveModelAsync(model, cancellationToken);
    }

    private async Task UnloadModelAsync(IServiceProvider serviceProvider, string model, CancellationToken cancellationToken)
    {
        operations.UpdateCurrent($"Unloading {model}", "Releasing the benchmarked Foundry model from the active runtime…");
        var catalogClient = serviceProvider.GetRequiredService<IFoundryCatalogClient>();
        await catalogClient.UnloadModelAsync(model, cancellationToken);
    }

    private async Task RefreshAvailabilityAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
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

    private static IReadOnlyList<string> GetLoadedAliases(FoundryCatalogSnapshot snapshot)
        => snapshot.Models
            .Where(static model => model.IsLoaded)
            .Select(static model => model.Alias)
            .Concat((snapshot.Availability.RunningModels ?? Array.Empty<LlmRunningModel>()).Select(static model => model.Name))
            .Where(static alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string BuildRuntimePathSummary(FoundryAccelerationSnapshot acceleration)
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

    private static ModelBenchmarkResult AppendBenchmarkNote(ModelBenchmarkResult result, string note)
        => string.IsNullOrWhiteSpace(note)
            ? result
            : result with { Notes = [.. result.Notes, note] };

    private static bool ShouldRegisterAcceleration(FoundryAccelerationSnapshot acceleration)
        => acceleration.Readiness is FoundryAccelerationReadiness.NeedsRegistration or FoundryAccelerationReadiness.PartiallyReady;

    private IReadOnlyList<string> GetPreferredExecutionProviders()
        => foundryOptions.PreferredExecutionProviders
            .Where(static providerName => !string.IsNullOrWhiteSpace(providerName))
            .Select(static providerName => providerName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool ShouldRemoveModelAfterBenchmark(ModelBenchmarkResult result)
        => result.Fit == OllamaCapacityFit.TooLargeForInteractive;

    private sealed record FoundryBenchmarkIsolationResult(FoundryCatalogSnapshot Snapshot, IReadOnlyList<string> Notes);
}

public sealed record FoundryBenchmarkPreparationResult(FoundryCatalogSnapshot Snapshot, IReadOnlyList<string> Notes);