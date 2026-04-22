using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;

namespace LiCvWriter.Tests.Application;

public sealed class OllamaModelBenchmarkServiceTests
{
    private static readonly OllamaOptions Options = new()
    {
        CapacityWarmupNumPredict = 32,
        CapacityTooSlowTokensPerSecond = 8.0,
        CapacityComfortableTokensPerSecond = 25.0
    };

    [Fact]
    public async Task RunSingleAsync_HappyPath_ProducesScoredResult()
    {
        var perfectJson = """
            {
              "roleTitle": "Senior Backend Engineer",
              "companyName": "Acme Robotics",
              "mustHaveThemes": ["go", "kubernetes", "distributed systems"]
            }
            """;

        var llmClient = new ScriptedLlmClient([
            // Warm-up call (capacity probe). 64 tok/s decode.
            new LlmResponse("m:test", "ready", null, true, 4, 64, TimeSpan.FromSeconds(1.0),
                LoadDuration: TimeSpan.FromSeconds(0.5),
                PromptEvalDuration: TimeSpan.FromSeconds(0.1),
                EvalDuration: TimeSpan.FromSeconds(1.0)),
            // Quality call.
            new LlmResponse("m:test", perfectJson, null, true, 50, 30, TimeSpan.FromSeconds(1.0))
        ]);
        llmClient.Running = new OllamaRunningModel("m:test", "m:test", null, SizeVramBytes: 4_000_000_000, SizeBytes: 4_000_000_000);

        var probe = new OllamaCapacityProbe(llmClient, Options);
        var benchmark = new OllamaModelBenchmarkService(probe, llmClient, Options);

        var result = await benchmark.RunSingleAsync("m:test");

        Assert.True(result.Succeeded);
        Assert.Null(result.FailedReason);
        Assert.InRange(result.QualityScore, 0.99, 1.0);
        Assert.InRange(result.OverallScore, 0.0, 1.0);
        Assert.True(result.OverallScore > 0.6); // 0.6 * 1.0 quality + 0.4 * (64/60) clamped = 1.0
        Assert.NotNull(result.DecodeTokensPerSecond);
        Assert.Equal(OllamaCapacityFit.Comfortable, result.Fit);
    }

    [Fact]
    public async Task RunSingleAsync_QualityResponseIsGarbage_ReturnsLowQualityScore()
    {
        var llmClient = new ScriptedLlmClient([
            new LlmResponse("m:test", "ready", null, true, 4, 64, TimeSpan.FromSeconds(1.0),
                LoadDuration: TimeSpan.Zero,
                PromptEvalDuration: TimeSpan.FromSeconds(0.1),
                EvalDuration: TimeSpan.FromSeconds(1.0)),
            // Quality response: invalid JSON. LlmJsonInvoker will trigger a repair attempt.
            new LlmResponse("m:test", "definitely not json", null, true, 5, 5, TimeSpan.FromSeconds(0.5)),
            // Repair attempt also fails.
            new LlmResponse("m:test", "still not json", null, true, 5, 5, TimeSpan.FromSeconds(0.5))
        ]);
        llmClient.Running = new OllamaRunningModel("m:test", "m:test", null, SizeVramBytes: 4_000_000_000, SizeBytes: 4_000_000_000);

        var probe = new OllamaCapacityProbe(llmClient, Options);
        var benchmark = new OllamaModelBenchmarkService(probe, llmClient, Options);

        var result = await benchmark.RunSingleAsync("m:test");

        Assert.True(result.Succeeded);
        Assert.Equal(0.0, result.QualityScore);
        // Speed half still contributes: 0.4 * clamp(64/60) ≈ 0.4
        Assert.InRange(result.OverallScore, 0.39, 0.41);
    }

    [Fact]
    public async Task RunSingleAsync_QualityCallThrows_ReturnsFailedResultWithReason()
    {
        var llmClient = new ScriptedLlmClient([
            new LlmResponse("m:test", "ready", null, true, 4, 64, TimeSpan.FromSeconds(1.0),
                LoadDuration: TimeSpan.Zero,
                PromptEvalDuration: TimeSpan.FromSeconds(0.1),
                EvalDuration: TimeSpan.FromSeconds(1.0))
        ])
        {
            ThrowOnCall = 2 // throw on second GenerateAsync (the quality call)
        };
        llmClient.Running = new OllamaRunningModel("m:test", "m:test", null, SizeVramBytes: 4_000_000_000, SizeBytes: 4_000_000_000);

        var probe = new OllamaCapacityProbe(llmClient, Options);
        var benchmark = new OllamaModelBenchmarkService(probe, llmClient, Options);

        var result = await benchmark.RunSingleAsync("m:test");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailedReason);
        Assert.Contains("Quality call failed", result.FailedReason!, StringComparison.Ordinal);
        // Capacity verdict still recorded.
        Assert.Equal(OllamaCapacityFit.Comfortable, result.Fit);
    }

    [Fact]
    public async Task RunSingleAsync_CancellationDuringQualityCall_PropagatesOperationCanceled()
    {
        var llmClient = new ScriptedLlmClient([
            new LlmResponse("m:test", "ready", null, true, 4, 64, TimeSpan.FromSeconds(1.0),
                LoadDuration: TimeSpan.Zero,
                PromptEvalDuration: TimeSpan.FromSeconds(0.1),
                EvalDuration: TimeSpan.FromSeconds(1.0))
        ]);
        llmClient.Running = new OllamaRunningModel("m:test", "m:test", null, SizeVramBytes: 4_000_000_000, SizeBytes: 4_000_000_000);

        var probe = new OllamaCapacityProbe(llmClient, Options);
        var benchmark = new OllamaModelBenchmarkService(probe, llmClient, Options);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => benchmark.RunSingleAsync("m:test", cts.Token));
    }

    [Fact]
    public async Task RunSingleAsync_EmptyModel_ReturnsFailedResult()
    {
        var llmClient = new ScriptedLlmClient([]);
        var probe = new OllamaCapacityProbe(llmClient, Options);
        var benchmark = new OllamaModelBenchmarkService(probe, llmClient, Options);

        var result = await benchmark.RunSingleAsync("");

        Assert.False(result.Succeeded);
        Assert.Equal("Empty model name.", result.FailedReason);
    }

    private sealed class ScriptedLlmClient(LlmResponse[] responses) : ILlmClient
    {
        private int callIndex;
        public OllamaRunningModel? Running { get; set; }
        public int ThrowOnCall { get; set; } = -1;

        public Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
        {
            var running = Running is null ? Array.Empty<OllamaRunningModel>() : new[] { Running };
            return Task.FromResult(new OllamaModelAvailability(
                Version: "0.0.0",
                Model: Running?.Name ?? string.Empty,
                Installed: true,
                AvailableModels: Array.Empty<string>(),
                RunningModels: running));
        }

        public Task<LlmResponse> GenerateAsync(LlmRequest request, Action<LlmProgressUpdate>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            callIndex++;
            if (ThrowOnCall == callIndex)
            {
                throw new InvalidOperationException("scripted failure");
            }

            var responseIndex = Math.Min(callIndex - 1, responses.Length - 1);
            return Task.FromResult(responses[responseIndex]);
        }

        public Task<OllamaModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<OllamaModelInfo?>(null);
    }
}
