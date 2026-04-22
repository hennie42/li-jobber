using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
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

    private static (ModelBenchmarkCoordinator coordinator, WorkspaceSession workspace) BuildCoordinator(ILlmClient llmClient)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options);
        services.AddSingleton(llmClient);
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

        public Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new OllamaModelAvailability(
                Version: "0.0.0",
                Model: string.Empty,
                Installed: true,
                AvailableModels: Array.Empty<string>(),
                RunningModels: queues.Keys
                    .Select(static name => new OllamaRunningModel(name, name, null, SizeVramBytes: 4_000_000_000, SizeBytes: 4_000_000_000))
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

        public Task<OllamaModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<OllamaModelInfo?>(null);
    }

    private sealed class GatedLlmClient(Task gate) : ILlmClient
    {
        public Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new OllamaModelAvailability("0.0.0", string.Empty, true, Array.Empty<string>(), Array.Empty<OllamaRunningModel>()));

        public async Task<LlmResponse> GenerateAsync(LlmRequest request, Action<LlmProgressUpdate>? progress = null, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new LlmResponse(request.Model, "ready", null, true, 4, 32, TimeSpan.FromSeconds(1.0),
                LoadDuration: TimeSpan.Zero,
                PromptEvalDuration: TimeSpan.FromSeconds(0.1),
                EvalDuration: TimeSpan.FromSeconds(1.0));
        }

        public Task<OllamaModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<OllamaModelInfo?>(null);
    }
}
