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
    FoundryBenchmarkLifecycleService foundryLifecycle,
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
                    var foundryPreparation = await foundryLifecycle.PrepareAsync(
                        scope.ServiceProvider,
                        model,
                        downloadMissingModels,
                        progress => ReportLiveProgress(model, index, models.Count, results.ToArray(), progress, hangClock),
                        modelLifetimeToken);
                    preparationStopwatch.Stop();
                    foundryAcceleration = foundryPreparation.Snapshot.Acceleration;
                    foundryPreparationNotes = foundryPreparation.Notes;
                    modelDiagnostics = MergeDiagnostics(
                        modelDiagnostics,
                        FoundryBenchmarkLifecycleService.CreatePreparationDiagnostics(foundryAcceleration, preparationStopwatch.Elapsed));
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
                    result = FoundryBenchmarkLifecycleService.ApplyAccelerationNotes(result, foundryAcceleration);
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
                    result = await foundryLifecycle.FinalizeAsync(
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

    internal static ModelBenchmarkDiagnostics MergeDiagnostics(
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
            currentPhase = ModelBenchmarkRunPhase.Preparing;
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
                var warningAfter = policy.GetWarningAfter(currentPhase);
                if (HangState == ModelBenchmarkHangState.Warning || (now - LastRealProgressUtc) < warningAfter)
                {
                    return false;
                }

                HangState = ModelBenchmarkHangState.Warning;
                WarningStartedUtc = now;
                DeadlineUtc = now + policy.GetGracePeriod(currentPhase);
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
