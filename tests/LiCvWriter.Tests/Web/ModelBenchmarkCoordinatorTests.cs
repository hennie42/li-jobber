using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using LiCvWriter.Infrastructure.Llm;
using LiCvWriter.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LiCvWriter.Tests.Web;

public sealed class ModelBenchmarkCoordinatorTests
{
    private static readonly OllamaOptions Options = new()
    {
        CapacityWarmupNumPredict = 32,
        CapacityTooSlowTokensPerSecond = 8.0,
        CapacityComfortableTokensPerSecond = 25.0,
        MaxOperationSeconds = 0
    };

    private static readonly ModelBenchmarkHangClockPolicy DefaultHangClockPolicy = ModelBenchmarkHangClockPolicy.Default;

    private static readonly ModelBenchmarkHangClockPolicy FastHangClockPolicy = new(
        WarningAfter: TimeSpan.FromMilliseconds(120),
        GracePeriod: TimeSpan.FromMilliseconds(160),
        PollInterval: TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task StartAsync_RanksMultipleModels_DescendingByOverallScore()
    {
        var llmClient = new ScriptedLlmClient(BuildPerfectResponses("fast"), BuildPerfectResponses("slow", evalSeconds: 10.0));
        var (coordinator, workspace) = BuildCoordinator(llmClient);

        await coordinator.StartAsync(["fast", "slow"]);

        var session = coordinator.Last;
        Assert.NotNull(session);
        Assert.False(session!.IsRunning);
        Assert.False(session.IsCancelled);
        Assert.Equal(2, session.Results.Count);
        Assert.Equal(1, session.Results[0].Rank);
        Assert.Equal(2, session.Results[1].Rank);
        Assert.True(session.Results[0].OverallScore >= session.Results[1].OverallScore);
        Assert.Equal("fast", session.Results[0].Model);
        Assert.Same(session, workspace.LastBenchmarkSession);
    }

    [Fact]
    public async Task StartAsync_FailingModel_GetsRowWithFailedReason_AndRunContinues()
    {
        var llmClient = new ScriptedLlmClient(
            BuildPerfectResponses("ok"),
            BuildFailingProbeResponses("broken"));
        var (coordinator, _) = BuildCoordinator(llmClient);

        await coordinator.StartAsync(["ok", "broken"]);

        var session = coordinator.Last!;
        Assert.Equal(2, session.Results.Count);
        var brokenResult = session.Results.Single(r => r.Model == "broken");
        Assert.False(brokenResult.Succeeded);
        Assert.NotNull(brokenResult.FailedReason);
        var okResult = session.Results.Single(r => r.Model == "ok");
        Assert.True(okResult.Succeeded);
        Assert.Equal(1, okResult.Rank);
    }

    [Fact]
    public async Task Cancel_BeforeFirstModelCompletes_ShortCircuitsAndMarksCancelled()
    {
        // Block on the first call until we cancel.
        var releaseFirstCall = new TaskCompletionSource();
        var llmClient = new GatedLlmClient(releaseFirstCall.Task);
        var (coordinator, _) = BuildCoordinator(llmClient);

        var runTask = coordinator.StartAsync(["a", "b", "c"]);

        coordinator.Cancel();
        releaseFirstCall.SetResult();
        await runTask;

        var session = coordinator.Last!;
        Assert.True(session.IsCancelled);
        Assert.False(session.IsRunning);
        // Model "a" likely either completed or threw OperationCanceled before we got further;
        // critically, "b" and "c" were never started.
        Assert.DoesNotContain(session.Results, r => r.Model == "c");
    }

    [Fact]
    public async Task StartAsync_FiresChangedEventPerProgressTransition()
    {
        var llmClient = new ScriptedLlmClient(BuildPerfectResponses("a"), BuildPerfectResponses("b"));
        var (coordinator, _) = BuildCoordinator(llmClient);
        var changedCount = 0;
        coordinator.Changed += () => Interlocked.Increment(ref changedCount);

        await coordinator.StartAsync(["a", "b"]);

        // Per model we trigger UpdateProgress before and after the run, plus a final RunAsync notify.
        // Concrete count is implementation-detail; require strictly more than one.
        Assert.True(changedCount >= 2, $"Expected multiple Changed events, got {changedCount}.");
    }

    [Fact]
    public async Task StartAsync_WhenRunningBenchmark_PublishesTypedFixtureProgress()
    {
        var llmClient = new ScriptedLlmClient(BuildPerfectResponses("a"));
        var (coordinator, _) = BuildCoordinator(llmClient);
        var observedSessions = new List<ModelBenchmarkSession>();

        coordinator.Changed += () =>
        {
            if (coordinator.Current is { IsRunning: true } current)
            {
                observedSessions.Add(current);
            }
        };

        await coordinator.StartAsync(["a"]);

        Assert.Contains(observedSessions, session => session.CurrentPhase == ModelBenchmarkRunPhase.Warmup);
        Assert.Contains(observedSessions, session => session.CurrentPhase == ModelBenchmarkRunPhase.Evaluating);
        Assert.Contains(observedSessions, session => session.CurrentFixtureDisplayName == ModelBenchmarkFixtures.FixtureDisplayName);
        Assert.Contains(observedSessions, session => session.TotalFixtureCount == ModelBenchmarkFixtures.DefaultSuite.Count);
        Assert.Equal(ModelBenchmarkRunPhase.Completed, coordinator.Last!.CurrentPhase);
        Assert.Equal(ModelBenchmarkFixtures.DefaultSuite.Count, coordinator.Last.TotalFixtureCount);
    }

    [Fact]
    public async Task StartAsync_WhenLaterModelsAreStillRunning_PublishesRankedPartialResults()
    {
        var releaseSlowModel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llmClient = new SelectivelyGatedLlmClient(
            releaseSlowModel.Task,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "slow" },
            BuildPerfectResponses("fast"),
            BuildPerfectResponses("slow", evalSeconds: 10.0));
        var (coordinator, _) = BuildCoordinator(llmClient);
        var partialResultsPublished = new TaskCompletionSource<ModelBenchmarkSession>(TaskCreationOptions.RunContinuationsAsynchronously);

        coordinator.Changed += () =>
        {
            if (coordinator.Current is { IsRunning: true, Results.Count: > 0 } current
                && current.Results.All(static result => result.Rank > 0))
            {
                partialResultsPublished.TrySetResult(current);
            }
        };

        var runTask = coordinator.StartAsync(["fast", "slow"]);
        var partialSession = await partialResultsPublished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(partialSession.Results);
        Assert.Equal("fast", partialSession.Results[0].Model);
        Assert.Equal(1, partialSession.Results[0].Rank);
        Assert.Equal(1, partialSession.CompletedCount);
        Assert.Equal("slow", partialSession.CurrentModel);

        releaseSlowModel.SetResult();
        await runTask;
    }

    [Fact]
    public async Task StartAsync_WhenModelStopsMakingProgress_WarnsFailsModelAndContinuesQueue()
    {
        var blockedModel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llmClient = new SelectivelyGatedLlmClient(
            blockedModel.Task,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hung" },
            BuildPerfectResponses("hung"),
            BuildPerfectResponses("healthy"));
        var (coordinator, _) = BuildCoordinator(llmClient, hangClockPolicy: FastHangClockPolicy);
        var warningPublished = new TaskCompletionSource<ModelBenchmarkSession>(TaskCreationOptions.RunContinuationsAsynchronously);

        coordinator.Changed += () =>
        {
            if (coordinator.Current is { IsRunning: true, CurrentModel: "hung", HangState: ModelBenchmarkHangState.Warning } current)
            {
                warningPublished.TrySetResult(current);
            }
        };

        var runTask = coordinator.StartAsync(["hung", "healthy"]);
        var warningSession = await warningPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runTask;

        Assert.Equal(ModelBenchmarkHangState.Warning, warningSession.HangState);
        Assert.Contains("No benchmark progress detected", warningSession.HangDetail, StringComparison.Ordinal);

        var session = coordinator.Last!;
        var hungResult = session.Results.Single(result => result.Model == "hung");
        var healthyResult = session.Results.Single(result => result.Model == "healthy");

        Assert.False(session.IsCancelled);
        Assert.False(hungResult.Succeeded);
        Assert.Contains("Benchmark hang detected", hungResult.FailedReason, StringComparison.Ordinal);
        Assert.True(healthyResult.Succeeded);
    }

    [Fact]
    public async Task StartAsync_WhenProgressResumesAfterWarning_ClearsWarningAndCompletesModel()
    {
        var releaseModel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llmClient = new SelectivelyGatedLlmClient(
            releaseModel.Task,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "recovering" },
            BuildPerfectResponses("recovering"));
        var (coordinator, _) = BuildCoordinator(llmClient, hangClockPolicy: FastHangClockPolicy);
        var warningPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warningCleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        coordinator.Changed += () =>
        {
            if (coordinator.Current is { IsRunning: true, CurrentModel: "recovering", HangState: ModelBenchmarkHangState.Warning })
            {
                warningPublished.TrySetResult();
            }

            if (warningPublished.Task.IsCompleted
                && coordinator.Current is { IsRunning: true, CurrentModel: "recovering", HangState: ModelBenchmarkHangState.None, CompletedFixtureCount: > 0 })
            {
                warningCleared.TrySetResult();
            }
        };

        var runTask = coordinator.StartAsync(["recovering"]);
        await warningPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseModel.SetResult();
        await warningCleared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runTask;

        var result = coordinator.Last!.Results.Single();

        Assert.True(result.Succeeded);
        Assert.Equal(ModelBenchmarkHangState.None, coordinator.Last.HangState);
    }

    [Fact]
    public async Task StartAsync_WhenFoundryDownloadKeepsReportingProgress_DoesNotClassifyRunAsHung()
    {
        var foundryCatalogClient = new SlowProgressFoundryCatalogClient(
            CreateFoundrySnapshot(cachedModels: Array.Empty<string>(), isCached: false),
            [0.0, 40.0, 80.0, 100.0],
            TimeSpan.FromMilliseconds(60));
        var foundryBridge = new ScriptedFoundrySdkBridge(BuildPerfectResponses("phi-foundry"), ["phi-foundry"]);
        var (coordinator, _) = BuildCoordinator(
            new ScriptedLlmClient(),
            foundryBridge,
            foundryCatalogClient,
            hangClockPolicy: FastHangClockPolicy);

        await coordinator.StartAsync(
            LlmProviderKind.Foundry,
            ["phi-foundry"],
            downloadMissingModels: true,
            removeTooLargeModelsAfterBenchmark: false);

        var session = coordinator.Last!;

        Assert.Single(session.Results);
        Assert.True(session.Results[0].Succeeded);
        Assert.Equal(ModelBenchmarkHangState.None, session.HangState);
        Assert.DoesNotContain(session.Results[0].Notes, note => note.Contains("hang", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartAsync_WhenFoundryDownloadExceedsTimeoutBudget_StillBenchmarksAfterDownloadCompletes()
    {
        var timedOptions = new OllamaOptions
        {
            CapacityWarmupNumPredict = 32,
            CapacityTooSlowTokensPerSecond = 8.0,
            CapacityComfortableTokensPerSecond = 25.0,
            MaxOperationSeconds = 1
        };
        var foundryCatalogClient = new SlowProgressFoundryCatalogClient(
            CreateFoundrySnapshot(cachedModels: Array.Empty<string>(), isCached: false),
            [0.0, 10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0],
            TimeSpan.FromMilliseconds(110));
        var foundryBridge = new ScriptedFoundrySdkBridge(BuildPerfectResponses("phi-foundry"), ["phi-foundry"]);
        var (coordinator, _) = BuildCoordinator(
            new ScriptedLlmClient(),
            foundryBridge,
            foundryCatalogClient,
            ollamaOptions: timedOptions,
            hangClockPolicy: FastHangClockPolicy);

        await coordinator.StartAsync(
            LlmProviderKind.Foundry,
            ["phi-foundry"],
            downloadMissingModels: true,
            removeTooLargeModelsAfterBenchmark: false);

        var result = coordinator.Last!.Results.Single();

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("Timed out", result.FailedReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_WhenFoundryDownloadIsQuietDuringPreparation_UsesPreparationHangBudget()
    {
        var preparationAwarePolicy = new ModelBenchmarkHangClockPolicy(
            WarningAfter: TimeSpan.FromMilliseconds(120),
            GracePeriod: TimeSpan.FromMilliseconds(160),
            PollInterval: TimeSpan.FromMilliseconds(20),
            PreparationWarningAfter: TimeSpan.FromMilliseconds(300),
            PreparationGracePeriod: TimeSpan.FromMilliseconds(220));
        var foundryCatalogClient = new SlowProgressFoundryCatalogClient(
            CreateFoundrySnapshot(cachedModels: Array.Empty<string>(), isCached: false),
            [0.0, 100.0],
            TimeSpan.FromMilliseconds(180));
        var foundryBridge = new ScriptedFoundrySdkBridge(BuildPerfectResponses("phi-foundry"), ["phi-foundry"]);
        var (coordinator, _) = BuildCoordinator(
            new ScriptedLlmClient(),
            foundryBridge,
            foundryCatalogClient,
            hangClockPolicy: preparationAwarePolicy);
        var warningObserved = false;

        coordinator.Changed += () =>
        {
            if (coordinator.Current is { CurrentPhase: ModelBenchmarkRunPhase.Preparing, HangState: ModelBenchmarkHangState.Warning })
            {
                warningObserved = true;
            }
        };

        await coordinator.StartAsync(
            LlmProviderKind.Foundry,
            ["phi-foundry"],
            downloadMissingModels: true,
            removeTooLargeModelsAfterBenchmark: false);

        var result = coordinator.Last!.Results.Single();

        Assert.False(warningObserved);
        Assert.True(result.Succeeded);
        Assert.Equal(ModelBenchmarkHangState.None, coordinator.Last.HangState);
    }

    [Fact]
    public async Task StartAsync_WhenFixtureStreamsRealInnerProgress_DoesNotClassifyModelAsHung()
    {
        var llmClient = new StreamingDelayedLlmClient(
            completionDelay: TimeSpan.FromMilliseconds(360),
            progressInterval: TimeSpan.FromMilliseconds(40),
            BuildPerfectResponses("streaming"));
        var (coordinator, _) = BuildCoordinator(llmClient, hangClockPolicy: FastHangClockPolicy);
        var warningObserved = false;

        coordinator.Changed += () =>
        {
            if (coordinator.Current is { HangState: ModelBenchmarkHangState.Warning })
            {
                warningObserved = true;
            }
        };

        await coordinator.StartAsync(["streaming"]);

        var result = coordinator.Last!.Results.Single();

        Assert.False(warningObserved);
        Assert.True(result.Succeeded);
        Assert.Equal(ModelBenchmarkHangState.None, coordinator.Last.HangState);
    }

    [Fact]
    public async Task StartAsync_WhenBenchmarkProgressIncludesLlmTelemetry_ForwardsItToOperationStatus()
    {
        var llmClient = new TelemetryStreamingLlmClient(BuildPerfectResponses("streaming"));
        var (coordinator, _, operations) = BuildCoordinatorWithOperations(llmClient);

        await coordinator.StartAsync(["streaming"]);

        var telemetry = operations.LastCompletedLlmTelemetry;
        Assert.NotNull(telemetry);
        Assert.Equal("streaming", telemetry!.Model);
        Assert.Equal(50, telemetry.PromptTokens);
        Assert.Equal(30, telemetry.CompletionTokens);
        Assert.Contains("synthetic reasoning", telemetry.ThinkingContent, StringComparison.Ordinal);
        Assert.Contains("detectedTechnologies", telemetry.ResponseContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_WhileAnotherRunActive_Throws()
    {
        var releaseFirst = new TaskCompletionSource();
        var llmClient = new GatedLlmClient(releaseFirst.Task);
        var (coordinator, _) = BuildCoordinator(llmClient);

        var first = coordinator.StartAsync(["a"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => coordinator.StartAsync(["b"])));

        releaseFirst.SetResult();
        await first;
    }

    [Fact]
    public async Task StartAsync_EmptyModelList_Throws()
    {
        var llmClient = new ScriptedLlmClient();
        var (coordinator, _) = BuildCoordinator(llmClient);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.StartAsync(Array.Empty<string>()));
    }

    [Fact]
    public async Task StartAsync_WhenProviderIsFoundry_PersistsFoundryProviderInSession()
    {
        var foundryResponses = BuildPerfectResponses("phi-foundry");
        var foundryBridge = new ScriptedFoundrySdkBridge(foundryResponses, ["phi-foundry"]);
        var foundryCatalogClient = new ScriptedFoundryCatalogClient(
            new FoundryCatalogSnapshot(
                new LlmModelAvailability(
                    Version: "1.0.0",
                    Model: "phi-foundry",
                    Installed: true,
                    AvailableModels: ["phi-foundry"],
                    RunningModels: Array.Empty<LlmRunningModel>(),
                    Provider: LlmProviderKind.Foundry),
                [new FoundryCatalogModel("phi-foundry", "Phi Foundry", "phi-foundry", 1024, true, false)],
                FoundryAccelerationSnapshot.Unsupported("Not available"),
                DateTimeOffset.UtcNow));
        var (coordinator, workspace) = BuildCoordinator(new ScriptedLlmClient(), foundryBridge, foundryCatalogClient);

        await coordinator.StartAsync(LlmProviderKind.Foundry, ["phi-foundry"]);

        var session = coordinator.Last;
        Assert.NotNull(session);
        Assert.Equal(LlmProviderKind.Foundry, session!.Provider);
        Assert.All(session.Results, result => Assert.Equal(LlmProviderKind.Foundry, result.Provider));
        Assert.Equal(LlmProviderKind.Foundry, workspace.LastBenchmarkSession!.Provider);
    }

    [Fact]
    public async Task StartAsync_WhenDifferentProvidersRun_PreservesCombinedBenchmarkHistory()
    {
        var foundryResponses = BuildPerfectResponses("phi-foundry");
        var foundryBridge = new ScriptedFoundrySdkBridge(foundryResponses, ["phi-foundry"]);
        var foundryCatalogClient = new ScriptedFoundryCatalogClient(
            new FoundryCatalogSnapshot(
                new LlmModelAvailability(
                    Version: "1.0.0",
                    Model: "phi-foundry",
                    Installed: true,
                    AvailableModels: ["phi-foundry"],
                    RunningModels: Array.Empty<LlmRunningModel>(),
                    Provider: LlmProviderKind.Foundry),
                [new FoundryCatalogModel("phi-foundry", "Phi Foundry", "phi-foundry", 1024, true, false)],
                FoundryAccelerationSnapshot.Unsupported("Not available"),
                DateTimeOffset.UtcNow));
        var (coordinator, workspace) = BuildCoordinator(new ScriptedLlmClient(BuildPerfectResponses("ollama-a")), foundryBridge, foundryCatalogClient);

        await coordinator.StartAsync(["ollama-a"]);
        await coordinator.StartAsync(LlmProviderKind.Foundry, ["phi-foundry"]);

        Assert.Equal(2, workspace.BenchmarkResultsHistory.Count);
        Assert.Contains(workspace.BenchmarkResultsHistory, result => result.Provider == LlmProviderKind.Ollama && result.Model == "ollama-a");
        Assert.Contains(workspace.BenchmarkResultsHistory, result => result.Provider == LlmProviderKind.Foundry && result.Model == "phi-foundry");
    }

    [Fact]
    public async Task StartAsync_WhenSameProviderAndModelRunAgain_ReplacesExistingHistoryRow()
    {
        var (coordinator, workspace) = BuildCoordinator(new ScriptedLlmClient(BuildPerfectResponses("repeat", evalSeconds: 2.0)));

        workspace.SetLastBenchmarkSession(new ModelBenchmarkSession(
            StartedUtc: DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedUtc: DateTimeOffset.UtcNow.AddMinutes(-4),
            IsRunning: false,
            IsCancelled: false,
            CompletedCount: 1,
            TotalCount: 1,
            CurrentModel: null,
            Results:
            [
                new ModelBenchmarkResult(
                    Model: "repeat",
                    Rank: 1,
                    OverallScore: 0.25,
                    QualityScore: 0.20,
                    DecodeTokensPerSecond: 4.0,
                    LoadDuration: null,
                    TotalDuration: TimeSpan.FromSeconds(10),
                    Fit: OllamaCapacityFit.CpuOnly,
                    Notes: [],
                    FailedReason: null,
                    Provider: LlmProviderKind.Ollama)
            ]));

        await coordinator.StartAsync(["repeat"]);

        Assert.Single(workspace.BenchmarkResultsHistory);
        Assert.Equal(LlmProviderKind.Ollama, workspace.BenchmarkResultsHistory[0].Provider);
        Assert.Equal("repeat", workspace.BenchmarkResultsHistory[0].Model);
        Assert.NotEqual(0.25, workspace.BenchmarkResultsHistory[0].OverallScore);
    }

    [Fact]
    public async Task StartAsync_FoundryRun_DownloadsBenchmarksRemovesBeforePublishingResult()
    {
        var callSequence = new List<string>();
        ModelBenchmarkCoordinator? coordinator = null;

        var foundryResponses = BuildPerfectResponses("phi-foundry", evalSeconds: 16.0);
        var foundryBridge = new ScriptedFoundrySdkBridge(
            foundryResponses,
            ["phi-foundry"],
            [new LlmRunningModel("phi-foundry", "phi-foundry", null, SizeVramBytes: 4_000_000_000, SizeBytes: 16_000_000_000, Provider: LlmProviderKind.Foundry)],
            (_, invocation) => callSequence.Add(invocation == 1 ? "benchmark" : "benchmark-followup"));

        var foundryCatalogClient = new RecordingFoundryCatalogClient(
            CreateFoundrySnapshot(cachedModels: Array.Empty<string>(), isCached: false),
            onDownload: _ =>
            {
                callSequence.Add("download");
                Assert.Empty(coordinator?.Current?.Results ?? Array.Empty<ModelBenchmarkResult>());
            },
            onRemove: _ =>
            {
                callSequence.Add("remove");
                Assert.Empty(coordinator?.Current?.Results ?? Array.Empty<ModelBenchmarkResult>());
            });

        (coordinator, _) = BuildCoordinator(new ScriptedLlmClient(), foundryBridge, foundryCatalogClient);

        await coordinator.StartAsync(
            LlmProviderKind.Foundry,
            ["phi-foundry"],
            downloadMissingModels: true,
            removeTooLargeModelsAfterBenchmark: true);

        var session = coordinator.Last;
        Assert.NotNull(session);
        Assert.Single(session!.Results);
        Assert.Contains(session.Results[0].Notes, note => note.Contains("Removed from the local Foundry cache after benchmark", StringComparison.Ordinal));

        var downloadIndex = callSequence.IndexOf("download");
        var benchmarkIndex = callSequence.IndexOf("benchmark");
        var removeIndex = callSequence.IndexOf("remove");

        Assert.True(downloadIndex >= 0, "Expected a download step.");
        Assert.True(benchmarkIndex > downloadIndex, "Expected the benchmark to run after download.");
        Assert.True(removeIndex > benchmarkIndex, "Expected removal to happen after benchmark generation and before the result was published.");
    }

    [Fact]
    public async Task StartAsync_FoundryRemovalFailure_PreservesBenchmarkRowAndAddsCleanupNote()
    {
        var foundryResponses = BuildPerfectResponses("phi-foundry", evalSeconds: 16.0);
        var foundryBridge = new ScriptedFoundrySdkBridge(
            foundryResponses,
            ["phi-foundry"],
            [new LlmRunningModel("phi-foundry", "phi-foundry", null, SizeVramBytes: 4_000_000_000, SizeBytes: 16_000_000_000, Provider: LlmProviderKind.Foundry)]);

        var foundryCatalogClient = new RecordingFoundryCatalogClient(
            CreateFoundrySnapshot(cachedModels: ["phi-foundry"], isCached: true),
            removeException: new InvalidOperationException("simulated remove failure"));

        var (coordinator, _) = BuildCoordinator(new ScriptedLlmClient(), foundryBridge, foundryCatalogClient);

        await coordinator.StartAsync(
            LlmProviderKind.Foundry,
            ["phi-foundry"],
            downloadMissingModels: true,
            removeTooLargeModelsAfterBenchmark: true);

        var session = coordinator.Last;
        Assert.NotNull(session);

        var result = session!.Results.Single();
        Assert.True(result.Succeeded);
        Assert.True(result.OverallScore > 0.0);
        Assert.Contains(result.Notes, note => note.Contains("could not be removed from the local Foundry cache", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_FoundryRun_WhenAccelerationNeedsRegistration_RegistersProvidersBeforeBenchmark()
    {
        var callSequence = new List<string>();
        var degradedAcceleration = new FoundryAccelerationSnapshot(
            IsSupported: true,
            IsEnabled: true,
            CanManageExecutionProviders: true,
            StatusMessage: "Foundry Local discovered 2 Windows ML execution provider(s), but none are registered yet.",
            ExecutionProviders:
            [
                new FoundryExecutionProviderInfo("dml", "DirectML", false),
                new FoundryExecutionProviderInfo("cuda", "CUDA", false)
            ],
            CollectedAtUtc: DateTimeOffset.UtcNow);
        var readyAcceleration = new FoundryAccelerationSnapshot(
            IsSupported: true,
            IsEnabled: true,
            CanManageExecutionProviders: true,
            StatusMessage: "Foundry Local reports all 2 Windows ML execution provider(s) registered.",
            ExecutionProviders:
            [
                new FoundryExecutionProviderInfo("dml", "DirectML", true),
                new FoundryExecutionProviderInfo("cuda", "CUDA", true)
            ],
            CollectedAtUtc: DateTimeOffset.UtcNow);

        var foundryResponses = BuildPerfectResponses("phi-foundry", evalSeconds: 16.0);
        var foundryBridge = new ScriptedFoundrySdkBridge(
            foundryResponses,
            ["phi-foundry"],
            [new LlmRunningModel("phi-foundry", "phi-foundry", null, SizeVramBytes: 4_000_000_000, SizeBytes: 16_000_000_000, Provider: LlmProviderKind.Foundry)],
            (_, invocation) => callSequence.Add(invocation == 1 ? "benchmark" : "benchmark-followup"));
        var foundryCatalogClient = new RecordingFoundryCatalogClient(
            CreateFoundrySnapshot(cachedModels: ["phi-foundry"], isCached: true, acceleration: degradedAcceleration),
            registeredAcceleration: readyAcceleration,
            onRegister: providerNames =>
            {
                callSequence.Add("register");
                Assert.NotNull(providerNames);
                Assert.Contains("cuda", providerNames!, StringComparer.OrdinalIgnoreCase);
            });
        var foundryOptions = new FoundryOptions
        {
            DefaultModelAlias = "phi-foundry",
            UseWindowsMlAcceleration = true,
            PreferredExecutionProviders = ["cuda"]
        };

        var (coordinator, _) = BuildCoordinator(
            new ScriptedLlmClient(),
            foundryBridge,
            foundryCatalogClient,
            foundryOptions: foundryOptions);

        await coordinator.StartAsync(
            LlmProviderKind.Foundry,
            ["phi-foundry"],
            downloadMissingModels: true,
            removeTooLargeModelsAfterBenchmark: false);

        var registerIndex = callSequence.IndexOf("register");
        var benchmarkIndex = callSequence.IndexOf("benchmark");

        Assert.True(registerIndex >= 0, "Expected Foundry execution providers to be registered before benchmark.");
        Assert.True(benchmarkIndex > registerIndex, "Expected provider registration to happen before Foundry generation.");
        Assert.DoesNotContain(coordinator.Last!.Results.Single().Notes, note => note.Contains("Foundry acceleration during benchmark", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartAsync_FoundryRun_CapturesPreparationAndRuntimeDiagnostics()
    {
        var degradedAcceleration = new FoundryAccelerationSnapshot(
            IsSupported: true,
            IsEnabled: true,
            CanManageExecutionProviders: true,
            StatusMessage: "Foundry Local discovered 1 Windows ML execution provider(s), but none are registered yet.",
            ExecutionProviders:
            [
                new FoundryExecutionProviderInfo("cuda", "CUDA", false)
            ],
            CollectedAtUtc: DateTimeOffset.UtcNow);
        var readyAcceleration = new FoundryAccelerationSnapshot(
            IsSupported: true,
            IsEnabled: true,
            CanManageExecutionProviders: true,
            StatusMessage: "Foundry Local reports all 1 Windows ML execution provider(s) registered.",
            ExecutionProviders:
            [
                new FoundryExecutionProviderInfo("cuda", "CUDA", true)
            ],
            CollectedAtUtc: DateTimeOffset.UtcNow);
        var foundryCatalogClient = new RecordingFoundryCatalogClient(
            CreateFoundrySnapshot(cachedModels: ["phi-foundry"], isCached: true, acceleration: degradedAcceleration),
            registeredAcceleration: readyAcceleration);
        var foundryBridge = new ScriptedFoundrySdkBridge(BuildPerfectResponses("phi-foundry"), ["phi-foundry"]);
        var foundryOptions = new FoundryOptions
        {
            DefaultModelAlias = "phi-foundry",
            UseWindowsMlAcceleration = true,
            PreferredExecutionProviders = ["cuda"]
        };
        var (coordinator, _) = BuildCoordinator(
            new ScriptedLlmClient(),
            foundryBridge,
            foundryCatalogClient,
            foundryOptions: foundryOptions);

        await coordinator.StartAsync(
            LlmProviderKind.Foundry,
            ["phi-foundry"],
            downloadMissingModels: true,
            removeTooLargeModelsAfterBenchmark: false);

        var result = coordinator.Last!.Results.Single();

        Assert.NotNull(result.Diagnostics.PreparationDuration);
        Assert.NotNull(result.Diagnostics.WarmupDuration);
        Assert.NotNull(result.Diagnostics.EvaluationDuration);
        Assert.NotNull(result.Diagnostics.CleanupDuration);
        Assert.Equal(FoundryAccelerationReadiness.Ready.ToString(), result.Diagnostics.AccelerationReadiness);
        Assert.Contains("CUDA", result.Diagnostics.RuntimePathSummary ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("registered", result.Diagnostics.AccelerationStatusMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_FoundryRun_RefreshesWorkspaceAvailabilityBeforeBenchmarkGeneration()
    {
        var uncachedSnapshot = CreateFoundrySnapshot(cachedModels: Array.Empty<string>(), isCached: false);
        var cachedSnapshot = CreateFoundrySnapshot(cachedModels: ["phi-foundry"], isCached: true);
        WorkspaceSession? workspace = null;
        var foundryBridge = new ScriptedFoundrySdkBridge(
            BuildPerfectResponses("phi-foundry"),
            ["phi-foundry"],
            onGenerate: (_, _) =>
            {
                Assert.NotNull(workspace);
                Assert.Contains("phi-foundry", workspace!.FoundryAvailability?.AvailableModels ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            });
        var foundryCatalogClient = new RecordingFoundryCatalogClient(
            uncachedSnapshot,
            snapshotAfterDownload: cachedSnapshot);

        var buildResult = BuildCoordinator(new ScriptedLlmClient(), foundryBridge, foundryCatalogClient);
        var coordinator = buildResult.coordinator;
        workspace = buildResult.workspace;

        await coordinator.StartAsync(
            LlmProviderKind.Foundry,
            ["phi-foundry"],
            downloadMissingModels: true,
            removeTooLargeModelsAfterBenchmark: false);

        Assert.Contains("phi-foundry", workspace.FoundryAvailability?.AvailableModels ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_FoundryRun_UnloadsCompletedModelBeforeBenchmarkingNextModel()
    {
        var callSequence = new List<string>();
        var foundryBridge = new ScriptedFoundrySdkBridge(
            [.. BuildPerfectResponses("deepseek-r1-14b"), .. BuildPerfectResponses("deepseek-r1-7b")],
            ["deepseek-r1-14b", "deepseek-r1-7b"],
            onGenerate: (model, invocation) =>
            {
                callSequence.Add($"generate:{model}:{invocation}");
            });
        var foundryCatalogClient = new RecordingFoundryCatalogClient(
            CreateFoundrySnapshot(cachedModels: ["deepseek-r1-14b", "deepseek-r1-7b"], isCached: true),
            onUnload: alias => callSequence.Add($"unload:{alias}"));
        var (coordinator, _) = BuildCoordinator(new ScriptedLlmClient(), foundryBridge, foundryCatalogClient);

        await coordinator.StartAsync(
            LlmProviderKind.Foundry,
            ["deepseek-r1-14b", "deepseek-r1-7b"],
            downloadMissingModels: false,
            removeTooLargeModelsAfterBenchmark: false);

        var unloadIndex = callSequence.IndexOf("unload:deepseek-r1-14b");
        var nextModelGenerateIndex = callSequence.FindIndex(entry => entry.StartsWith("generate:deepseek-r1-7b:", StringComparison.Ordinal));

        Assert.True(unloadIndex >= 0, "Expected the completed Foundry model to be unloaded during cleanup.");
        Assert.True(nextModelGenerateIndex > unloadIndex, "Expected Foundry cleanup to unload the completed model before the next model starts generating.");
    }

    [Fact]
    public async Task StartAsync_FoundryRun_WhenEarlierModelStillLoaded_UnloadsItBeforeBenchmarkStarts()
    {
        var callSequence = new List<string>();
        var snapshot = CreateFoundrySnapshot(cachedModels: ["deepseek-r1-7b"], isCached: true) with
        {
            Availability = CreateFoundrySnapshot(cachedModels: ["deepseek-r1-7b"], isCached: true).Availability with
            {
                RunningModels =
                [
                    new LlmRunningModel(
                        "deepseek-r1-14b",
                        "deepseek-r1-14b",
                        null,
                        SizeVramBytes: 4_000_000_000,
                        SizeBytes: 16_000_000_000,
                        Provider: LlmProviderKind.Foundry)
                ]
            }
        };
        var foundryBridge = new ScriptedFoundrySdkBridge(BuildPerfectResponses("deepseek-r1-7b"), ["deepseek-r1-7b"], onGenerate: (model, invocation) => callSequence.Add($"generate:{model}:{invocation}"));
        var foundryCatalogClient = new RecordingFoundryCatalogClient(
            snapshot,
            onUnload: alias => callSequence.Add($"unload:{alias}"));
        var (coordinator, _) = BuildCoordinator(new ScriptedLlmClient(), foundryBridge, foundryCatalogClient);

        await coordinator.StartAsync(
            LlmProviderKind.Foundry,
            ["deepseek-r1-7b"],
            downloadMissingModels: false,
            removeTooLargeModelsAfterBenchmark: false);

        var preloadUnloadIndex = callSequence.IndexOf("unload:deepseek-r1-14b");
        var generateIndex = callSequence.FindIndex(entry => entry.StartsWith("generate:deepseek-r1-7b:", StringComparison.Ordinal));

        Assert.True(preloadUnloadIndex >= 0, "Expected stale Foundry runtime state to be unloaded before the benchmark starts.");
        Assert.True(generateIndex > preloadUnloadIndex, "Expected the benchmarked model to start only after stale Foundry models were unloaded.");
    }

    [Fact]
    public async Task StartAsync_FoundryRun_WhenIsolationUnloadFails_AddsNoteAndContinuesBenchmark()
    {
        var snapshot = CreateFoundrySnapshot(cachedModels: ["deepseek-r1-7b"], isCached: true) with
        {
            Availability = CreateFoundrySnapshot(cachedModels: ["deepseek-r1-7b"], isCached: true).Availability with
            {
                RunningModels =
                [
                    new LlmRunningModel(
                        "deepseek-r1-14b",
                        "deepseek-r1-14b",
                        null,
                        SizeVramBytes: 4_000_000_000,
                        SizeBytes: 16_000_000_000,
                        Provider: LlmProviderKind.Foundry)
                ]
            }
        };
        var foundryBridge = new ScriptedFoundrySdkBridge(BuildPerfectResponses("deepseek-r1-7b"), ["deepseek-r1-7b"]);
        var foundryCatalogClient = new RecordingFoundryCatalogClient(
            snapshot,
            unloadExceptionFactory: alias => alias.Equals("deepseek-r1-14b", StringComparison.OrdinalIgnoreCase)
                ? new InvalidOperationException("stale unload failed")
                : null);
        var (coordinator, _) = BuildCoordinator(new ScriptedLlmClient(), foundryBridge, foundryCatalogClient);

        await coordinator.StartAsync(
            LlmProviderKind.Foundry,
            ["deepseek-r1-7b"],
            downloadMissingModels: false,
            removeTooLargeModelsAfterBenchmark: false);

        var result = coordinator.Last!.Results.Single();

        Assert.True(result.Succeeded);
        Assert.Contains(result.Notes, note => note.Contains("previously loaded model 'deepseek-r1-14b'", StringComparison.Ordinal));
    }

    private static FoundryCatalogSnapshot CreateFoundrySnapshot(
        IReadOnlyList<string> cachedModels,
        bool isCached,
        FoundryAccelerationSnapshot? acceleration = null)
        => new(
            new LlmModelAvailability(
                Version: "1.0.0",
                Model: cachedModels.FirstOrDefault() ?? string.Empty,
                Installed: cachedModels.Count > 0,
                AvailableModels: cachedModels,
                RunningModels: Array.Empty<LlmRunningModel>(),
                Provider: LlmProviderKind.Foundry),
            [new FoundryCatalogModel("phi-foundry", "Phi Foundry", "phi-foundry", 1024, isCached, false)],
            acceleration ?? FoundryAccelerationSnapshot.Unsupported("Not available"),
            DateTimeOffset.UtcNow);

    private static (ModelBenchmarkCoordinator coordinator, WorkspaceSession workspace) BuildCoordinator(
        ILlmClient llmClient,
        IFoundrySdkBridge? foundryBridge = null,
        IFoundryCatalogClient? foundryCatalogClient = null,
        OllamaOptions? ollamaOptions = null,
        FoundryOptions? foundryOptions = null,
        ModelBenchmarkHangClockPolicy? hangClockPolicy = null)
    {
        var (coordinator, workspace, _) = BuildCoordinatorWithOperations(
            llmClient,
            foundryBridge,
            foundryCatalogClient,
            ollamaOptions,
            foundryOptions,
            hangClockPolicy);

        return (coordinator, workspace);
    }

    private static (ModelBenchmarkCoordinator coordinator, WorkspaceSession workspace, OperationStatusService operations) BuildCoordinatorWithOperations(
        ILlmClient llmClient,
        IFoundrySdkBridge? foundryBridge = null,
        IFoundryCatalogClient? foundryCatalogClient = null,
        OllamaOptions? ollamaOptions = null,
        FoundryOptions? foundryOptions = null,
        ModelBenchmarkHangClockPolicy? hangClockPolicy = null)
    {
        var resolvedOllamaOptions = ollamaOptions ?? Options;
        var resolvedFoundryOptions = foundryOptions ?? new FoundryOptions();
        var resolvedHangClockPolicy = hangClockPolicy ?? DefaultHangClockPolicy;
        var services = new ServiceCollection();
        services.AddSingleton(resolvedOllamaOptions);
        services.AddSingleton(resolvedFoundryOptions);
        services.AddSingleton(resolvedHangClockPolicy);
        services.AddSingleton(llmClient);
        services.AddSingleton<IFoundrySdkBridge>(foundryBridge ?? new ScriptedFoundrySdkBridge(Array.Empty<LlmResponse>(), Array.Empty<string>()));
        services.AddSingleton<IFoundryCatalogClient>(foundryCatalogClient ?? new ScriptedFoundryCatalogClient(
            new FoundryCatalogSnapshot(
                new LlmModelAvailability(
                    Version: "1.0.0",
                    Model: string.Empty,
                    Installed: false,
                    AvailableModels: Array.Empty<string>(),
                    RunningModels: Array.Empty<LlmRunningModel>(),
                    Provider: LlmProviderKind.Foundry),
                Array.Empty<FoundryCatalogModel>(),
                FoundryAccelerationSnapshot.Unsupported("Not available"),
                DateTimeOffset.UtcNow)));
        services.AddScoped<FoundryLlmClient>();
        services.AddScoped<OllamaCapacityProbe>(sp => new OllamaCapacityProbe(sp.GetRequiredService<ILlmClient>(), resolvedOllamaOptions));
        services.AddScoped<OllamaModelBenchmarkService>(sp => new OllamaModelBenchmarkService(
            sp.GetRequiredService<OllamaCapacityProbe>(),
            sp.GetRequiredService<ILlmClient>(),
            resolvedOllamaOptions));
        var provider = services.BuildServiceProvider();

        var workspace = new WorkspaceSession(resolvedOllamaOptions, recoveryStore: null, foundryOptions: resolvedFoundryOptions);
        var operations = new OperationStatusService();
        var foundryLifecycle = new FoundryBenchmarkLifecycleService(
            workspace,
            operations,
            resolvedFoundryOptions,
            TimeProvider.System);
        var coordinator = new ModelBenchmarkCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            workspace,
            operations,
            resolvedOllamaOptions,
            foundryLifecycle,
            TimeProvider.System,
            resolvedHangClockPolicy);

        return (coordinator, workspace, operations);
    }

    private static LlmResponse[] BuildPerfectResponses(string model, double evalSeconds = 1.0)
    {
                var perfectJobExtractJson = """
            {
              "roleTitle": "Senior Backend Engineer",
              "companyName": "Acme Robotics",
              "mustHaveThemes": ["go", "kubernetes", "distributed systems"]
            }
            """;
                var perfectCompanyJson = """
                        {
                            "name": "Nordic Cloud Guild",
                            "guidingPrinciples": ["trust", "mentoring", "pragmatic delivery"],
                            "differentiators": ["knowledge sharing", "platform modernization"]
                        }
                        """;
                var perfectTechnologyGapJson = """
                        {
                            "detectedTechnologies": ["RAG", "vector search", "Kubernetes", "LLM evaluation"],
                            "possiblyUnderrepresentedTechnologies": ["Kubernetes", "LLM evaluation"]
                        }
                        """;
        return
        [
            // Warm-up call.
            new LlmResponse(model, "ready", null, true, 4, 64, TimeSpan.FromSeconds(evalSeconds),
                LoadDuration: TimeSpan.Zero,
                PromptEvalDuration: TimeSpan.FromSeconds(0.1),
                EvalDuration: TimeSpan.FromSeconds(evalSeconds)),
                        new LlmResponse(model, perfectJobExtractJson, null, true, 50, 30, TimeSpan.FromSeconds(1.0)),
                        new LlmResponse(model, perfectCompanyJson, null, true, 50, 30, TimeSpan.FromSeconds(1.0)),
                        new LlmResponse(model, perfectTechnologyGapJson, null, true, 50, 30, TimeSpan.FromSeconds(1.0))
        ];
    }

    private static LlmResponse[] BuildFailingProbeResponses(string model)
    {
        // Capacity probe will tolerate the warmup but the quality call throws via marker.
        return
        [
            new LlmResponse(model, "ready", null, true, 4, 64, TimeSpan.FromSeconds(1.0),
                LoadDuration: TimeSpan.Zero,
                PromptEvalDuration: TimeSpan.FromSeconds(0.1),
                EvalDuration: TimeSpan.FromSeconds(1.0)),
            // Marker: ScriptedLlmClient throws on this content.
            new LlmResponse(model, "__THROW__", null, true, 0, 0, TimeSpan.Zero)
        ];
    }

    private sealed class ScriptedLlmClient(params LlmResponse[][] perModelResponses) : ILlmClient
    {
        private readonly Dictionary<string, Queue<LlmResponse>> queues = perModelResponses
            .Where(static batch => batch.Length > 0)
            .ToDictionary(
                batch => batch[0].Model,
                batch => new Queue<LlmResponse>(batch),
                StringComparer.OrdinalIgnoreCase);

        public Task<LlmModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmModelAvailability(
                Version: "0.0.0",
                Model: string.Empty,
                Installed: true,
                AvailableModels: Array.Empty<string>(),
                RunningModels: queues.Keys
                    .Select(static name => new LlmRunningModel(name, name, null, SizeVramBytes: 4_000_000_000, SizeBytes: 4_000_000_000))
                    .ToArray()));

        public Task<LlmResponse> GenerateAsync(LlmRequest request, Action<LlmProgressUpdate>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!queues.TryGetValue(request.Model, out var queue) || queue.Count == 0)
            {
                throw new InvalidOperationException($"No scripted response for model '{request.Model}'.");
            }

            var response = queue.Dequeue();
            if (response.Content == "__THROW__")
            {
                throw new InvalidOperationException("scripted failure");
            }

            return Task.FromResult(response);
        }

        public Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<LlmModelInfo?>(null);
    }

    private sealed class GatedLlmClient(Task gate) : ILlmClient
    {
        public Task<LlmModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmModelAvailability("0.0.0", string.Empty, true, Array.Empty<string>(), Array.Empty<LlmRunningModel>()));

        public async Task<LlmResponse> GenerateAsync(LlmRequest request, Action<LlmProgressUpdate>? progress = null, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new LlmResponse(request.Model, "ready", null, true, 4, 32, TimeSpan.FromSeconds(1.0),
                LoadDuration: TimeSpan.Zero,
                PromptEvalDuration: TimeSpan.FromSeconds(0.1),
                EvalDuration: TimeSpan.FromSeconds(1.0));
        }

        public Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<LlmModelInfo?>(null);
    }

    private sealed class SelectivelyGatedLlmClient(
        Task gate,
        HashSet<string> gatedModels,
        params LlmResponse[][] perModelResponses) : ILlmClient
    {
        private readonly Dictionary<string, Queue<LlmResponse>> queues = perModelResponses
            .Where(static batch => batch.Length > 0)
            .ToDictionary(
                batch => batch[0].Model,
                batch => new Queue<LlmResponse>(batch),
                StringComparer.OrdinalIgnoreCase);

        public Task<LlmModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmModelAvailability(
                Version: "0.0.0",
                Model: string.Empty,
                Installed: true,
                AvailableModels: Array.Empty<string>(),
                RunningModels: queues.Keys
                    .Select(static name => new LlmRunningModel(name, name, null, SizeVramBytes: 4_000_000_000, SizeBytes: 4_000_000_000))
                    .ToArray()));

        public async Task<LlmResponse> GenerateAsync(LlmRequest request, Action<LlmProgressUpdate>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (gatedModels.Contains(request.Model))
            {
                await gate.WaitAsync(cancellationToken);
            }

            if (!queues.TryGetValue(request.Model, out var queue) || queue.Count == 0)
            {
                throw new InvalidOperationException($"No scripted response for model '{request.Model}'.");
            }

            var response = queue.Dequeue();
            if (response.Content == "__THROW__")
            {
                throw new InvalidOperationException("scripted failure");
            }

            return response;
        }

        public Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<LlmModelInfo?>(null);
    }

    private sealed class ScriptedFoundrySdkBridge(
        LlmResponse[] scriptedResponses,
        IReadOnlyList<string> availableModels,
        IReadOnlyList<LlmRunningModel>? runningModels = null,
        Action<string, int>? onGenerate = null) : IFoundrySdkBridge
    {
        private readonly Queue<LlmResponse> responses = new(scriptedResponses);
        private int generateCount;

        public Task<FoundryCatalogSnapshot> GetCatalogSnapshotAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FoundryAccelerationSnapshot> RegisterExecutionProvidersAsync(IReadOnlyList<string>? names = null, Action<string, double>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DownloadModelAsync(string alias, Action<double>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task UnloadModelAsync(string alias, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RemoveModelAsync(string alias, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LlmModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmModelAvailability(
                Version: "1.0.0",
                Model: availableModels.FirstOrDefault() ?? string.Empty,
                Installed: availableModels.Count > 0,
                AvailableModels: availableModels,
                RunningModels: runningModels ?? Array.Empty<LlmRunningModel>(),
                Provider: LlmProviderKind.Foundry));

        public Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<LlmModelInfo?>(null);

        public Task<LlmResponse> GenerateAsync(LlmRequest request, Action<LlmProgressUpdate>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            generateCount++;
            onGenerate?.Invoke(request.Model, generateCount);
            if (responses.Count == 0)
            {
                throw new InvalidOperationException($"No scripted Foundry response for model '{request.Model}'.");
            }

            return Task.FromResult(responses.Dequeue());
        }
    }

    private sealed class ScriptedFoundryCatalogClient(FoundryCatalogSnapshot snapshot) : IFoundryCatalogClient
    {
        public Task<FoundryCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<FoundryAccelerationSnapshot> RegisterExecutionProvidersAsync(IReadOnlyList<string>? names = null, Action<string, double>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot.Acceleration);

        public Task DownloadModelAsync(string alias, Action<double>? progress = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UnloadModelAsync(string alias, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveModelAsync(string alias, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingFoundryCatalogClient(
        FoundryCatalogSnapshot snapshot,
        FoundryAccelerationSnapshot? registeredAcceleration = null,
        FoundryCatalogSnapshot? snapshotAfterDownload = null,
        Action<IReadOnlyList<string>?>? onRegister = null,
        Action<string>? onDownload = null,
        Action<string>? onUnload = null,
        Action<string>? onRemove = null,
        Exception? removeException = null,
        Func<string, Exception?>? unloadExceptionFactory = null) : IFoundryCatalogClient
    {
        private FoundryCatalogSnapshot currentSnapshot = snapshot;
        private FoundryAccelerationSnapshot currentAcceleration = snapshot.Acceleration;

        public Task<FoundryCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(currentSnapshot with { Acceleration = currentAcceleration, CollectedAtUtc = DateTimeOffset.UtcNow });

        public Task<FoundryAccelerationSnapshot> RegisterExecutionProvidersAsync(IReadOnlyList<string>? names = null, Action<string, double>? progress = null, CancellationToken cancellationToken = default)
        {
            onRegister?.Invoke(names);
            progress?.Invoke(names?.FirstOrDefault() ?? "execution providers", 100.0);
            currentAcceleration = registeredAcceleration ?? currentAcceleration;
            return Task.FromResult(currentAcceleration);
        }

        public Task DownloadModelAsync(string alias, Action<double>? progress = null, CancellationToken cancellationToken = default)
        {
            onDownload?.Invoke(alias);
            currentSnapshot = snapshotAfterDownload ?? currentSnapshot;
            progress?.Invoke(100.0);
            return Task.CompletedTask;
        }

        public Task UnloadModelAsync(string alias, CancellationToken cancellationToken = default)
        {
            onUnload?.Invoke(alias);
            var unloadException = unloadExceptionFactory?.Invoke(alias);
            if (unloadException is not null)
            {
                return Task.FromException(unloadException);
            }

            var runningModels = currentSnapshot.Availability.RunningModels ?? Array.Empty<LlmRunningModel>();
            currentSnapshot = currentSnapshot with
            {
                Availability = currentSnapshot.Availability with
                {
                    RunningModels = runningModels
                        .Where(model => !model.Name.Equals(alias, StringComparison.OrdinalIgnoreCase)
                            && !model.Model.Equals(alias, StringComparison.OrdinalIgnoreCase))
                        .ToArray()
                },
                Models = currentSnapshot.Models
                    .Select(model => model.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)
                        ? model with { IsLoaded = false }
                        : model)
                    .ToArray(),
                CollectedAtUtc = DateTimeOffset.UtcNow
            };
            return Task.CompletedTask;
        }

        public Task RemoveModelAsync(string alias, CancellationToken cancellationToken = default)
        {
            onRemove?.Invoke(alias);
            return removeException is null
                ? Task.CompletedTask
                : Task.FromException(removeException);
        }
    }

    private sealed class SlowProgressFoundryCatalogClient(
        FoundryCatalogSnapshot snapshot,
        IReadOnlyList<double> progressSamples,
        TimeSpan delayBetweenSamples) : IFoundryCatalogClient
    {
        public Task<FoundryCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<FoundryAccelerationSnapshot> RegisterExecutionProvidersAsync(IReadOnlyList<string>? names = null, Action<string, double>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot.Acceleration);

        public async Task DownloadModelAsync(string alias, Action<double>? progress = null, CancellationToken cancellationToken = default)
        {
            foreach (var sample in progressSamples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(sample);
                await Task.Delay(delayBetweenSamples, cancellationToken);
            }
        }

        public Task UnloadModelAsync(string alias, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveModelAsync(string alias, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StreamingDelayedLlmClient(
        TimeSpan completionDelay,
        TimeSpan progressInterval,
        params LlmResponse[][] perModelResponses) : ILlmClient
    {
        private readonly Dictionary<string, Queue<LlmResponse>> queues = perModelResponses
            .Where(static batch => batch.Length > 0)
            .ToDictionary(
                batch => batch[0].Model,
                batch => new Queue<LlmResponse>(batch),
                StringComparer.OrdinalIgnoreCase);

        public Task<LlmModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmModelAvailability(
                Version: "0.0.0",
                Model: string.Empty,
                Installed: true,
                AvailableModels: Array.Empty<string>(),
                RunningModels: queues.Keys
                    .Select(static name => new LlmRunningModel(name, name, null, SizeVramBytes: 4_000_000_000, SizeBytes: 4_000_000_000))
                    .ToArray()));

        public async Task<LlmResponse> GenerateAsync(LlmRequest request, Action<LlmProgressUpdate>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!queues.TryGetValue(request.Model, out var queue) || queue.Count == 0)
            {
                throw new InvalidOperationException($"No scripted response for model '{request.Model}'.");
            }

            if (progress is not null)
            {
                var startedAt = DateTimeOffset.UtcNow;
                long sequence = 0;
                var emittedChars = 0;
                while (DateTimeOffset.UtcNow - startedAt < completionDelay)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    emittedChars += 24;
                    progress(new LlmProgressUpdate(
                        Message: "Generating response",
                        Detail: $"Synthetic stream update {sequence + 1}.",
                        Model: request.Model,
                        Elapsed: DateTimeOffset.UtcNow - startedAt,
                        Completed: false,
                        ResponseContent: new string('x', emittedChars),
                        Sequence: ++sequence));
                    await Task.Delay(progressInterval, cancellationToken);
                }
            }

            return queue.Dequeue();
        }

        public Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<LlmModelInfo?>(null);
    }

    private sealed class TelemetryStreamingLlmClient(params LlmResponse[][] perModelResponses) : ILlmClient
    {
        private readonly Dictionary<string, Queue<LlmResponse>> queues = perModelResponses
            .Where(static batch => batch.Length > 0)
            .ToDictionary(
                batch => batch[0].Model,
                batch => new Queue<LlmResponse>(batch),
                StringComparer.OrdinalIgnoreCase);

        public Task<LlmModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmModelAvailability(
                Version: "0.0.0",
                Model: string.Empty,
                Installed: true,
                AvailableModels: Array.Empty<string>(),
                RunningModels: queues.Keys
                    .Select(static name => new LlmRunningModel(name, name, null, SizeVramBytes: 4_000_000_000, SizeBytes: 4_000_000_000))
                    .ToArray()));

        public Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<LlmModelInfo?>(null);

        public Task<LlmResponse> GenerateAsync(LlmRequest request, Action<LlmProgressUpdate>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!queues.TryGetValue(request.Model, out var queue) || queue.Count == 0)
            {
                throw new InvalidOperationException($"No scripted response for model '{request.Model}'.");
            }

            var response = queue.Dequeue();
            var thinkingContent = $"synthetic reasoning for {request.Model}";
            var partialResponse = response.Content.Length <= 24 ? response.Content : response.Content[..24];

            progress?.Invoke(new LlmProgressUpdate(
                Message: "Generating response",
                Detail: "Synthetic stream update 1.",
                Model: request.Model,
                Elapsed: TimeSpan.FromMilliseconds(250),
                Completed: false,
                PromptTokens: response.PromptTokens,
                ResponseContent: partialResponse,
                ThinkingPreview: thinkingContent,
                ThinkingContent: thinkingContent,
                Sequence: 1));

            progress?.Invoke(new LlmProgressUpdate(
                Message: "Generating response",
                Detail: "Synthetic stream completed.",
                Model: request.Model,
                Elapsed: response.Duration,
                Completed: true,
                PromptTokens: response.PromptTokens,
                CompletionTokens: response.CompletionTokens,
                ResponseContent: response.Content,
                ThinkingPreview: thinkingContent,
                ThinkingContent: thinkingContent,
                Sequence: 2));

            return Task.FromResult(response with { Thinking = thinkingContent });
        }
    }
}
