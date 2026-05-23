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

    private static FoundryCatalogSnapshot CreateFoundrySnapshot(IReadOnlyList<string> cachedModels, bool isCached)
        => new(
            new LlmModelAvailability(
                Version: "1.0.0",
                Model: cachedModels.FirstOrDefault() ?? string.Empty,
                Installed: cachedModels.Count > 0,
                AvailableModels: cachedModels,
                RunningModels: Array.Empty<LlmRunningModel>(),
                Provider: LlmProviderKind.Foundry),
            [new FoundryCatalogModel("phi-foundry", "Phi Foundry", "phi-foundry", 1024, isCached, false)],
            FoundryAccelerationSnapshot.Unsupported("Not available"),
            DateTimeOffset.UtcNow);

    private static (ModelBenchmarkCoordinator coordinator, WorkspaceSession workspace) BuildCoordinator(
        ILlmClient llmClient,
        IFoundrySdkBridge? foundryBridge = null,
        IFoundryCatalogClient? foundryCatalogClient = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options);
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
        services.AddScoped<OllamaCapacityProbe>(sp => new OllamaCapacityProbe(sp.GetRequiredService<ILlmClient>(), Options));
        services.AddScoped<OllamaModelBenchmarkService>(sp => new OllamaModelBenchmarkService(
            sp.GetRequiredService<OllamaCapacityProbe>(),
            sp.GetRequiredService<ILlmClient>(),
            Options));
        var provider = services.BuildServiceProvider();

        var workspace = new WorkspaceSession(Options, recoveryStore: null);
        var coordinator = new ModelBenchmarkCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            workspace,
            new OperationStatusService(),
            Options,
            TimeProvider.System);

        return (coordinator, workspace);
    }

    private static LlmResponse[] BuildPerfectResponses(string model, double evalSeconds = 1.0)
    {
        var perfectJson = """
            {
              "roleTitle": "Senior Backend Engineer",
              "companyName": "Acme Robotics",
              "mustHaveThemes": ["go", "kubernetes", "distributed systems"]
            }
            """;
        return
        [
            // Warm-up call.
            new LlmResponse(model, "ready", null, true, 4, 64, TimeSpan.FromSeconds(evalSeconds),
                LoadDuration: TimeSpan.Zero,
                PromptEvalDuration: TimeSpan.FromSeconds(0.1),
                EvalDuration: TimeSpan.FromSeconds(evalSeconds)),
            // Quality call.
            new LlmResponse(model, perfectJson, null, true, 50, 30, TimeSpan.FromSeconds(1.0))
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

        public Task RemoveModelAsync(string alias, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingFoundryCatalogClient(
        FoundryCatalogSnapshot snapshot,
        Action<string>? onDownload = null,
        Action<string>? onRemove = null,
        Exception? removeException = null) : IFoundryCatalogClient
    {
        public Task<FoundryCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<FoundryAccelerationSnapshot> RegisterExecutionProvidersAsync(IReadOnlyList<string>? names = null, Action<string, double>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot.Acceleration);

        public Task DownloadModelAsync(string alias, Action<double>? progress = null, CancellationToken cancellationToken = default)
        {
            onDownload?.Invoke(alias);
            progress?.Invoke(100.0);
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
}
