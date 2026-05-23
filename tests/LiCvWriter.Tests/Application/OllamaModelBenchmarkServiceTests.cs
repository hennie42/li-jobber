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
    public void DefaultSuite_WhenSummed_UsesNormalizedWeights()
    {
        var totalWeight = ModelBenchmarkFixtures.DefaultSuite.Sum(static fixture => fixture.Weight);

        Assert.Equal(1.0, totalWeight, 3);
    }

    [Fact]
    public async Task RunSingleAsync_HappyPath_ProducesScoredResult()
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

        var llmClient = new ScriptedLlmClient([
            // Warm-up call (capacity probe). 64 tok/s decode.
            new LlmResponse("m:test", "ready", null, true, 4, 64, TimeSpan.FromSeconds(1.0),
                LoadDuration: TimeSpan.FromSeconds(0.5),
                PromptEvalDuration: TimeSpan.FromSeconds(0.1),
                EvalDuration: TimeSpan.FromSeconds(1.0)),
                        // Weighted benchmark fixture calls.
                        new LlmResponse("m:test", perfectJobExtractJson, null, true, 50, 30, TimeSpan.FromSeconds(1.0)),
                        new LlmResponse("m:test", perfectCompanyJson, null, true, 50, 30, TimeSpan.FromSeconds(1.0)),
                        new LlmResponse("m:test", perfectTechnologyGapJson, null, true, 50, 30, TimeSpan.FromSeconds(1.0))
        ]);
        llmClient.Running = new LlmRunningModel("m:test", "m:test", null, SizeVramBytes: 4_000_000_000, SizeBytes: 4_000_000_000);

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
        Assert.Equal(ModelBenchmarkFixtures.DefaultSuite.Count, result.FixtureResults.Count);
        Assert.Equal(ModelBenchmarkFixtures.DefaultSuite.Count, result.FixtureResults.Select(static fixture => fixture.FixtureId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(result.FixtureResults, fixture => Assert.Equal(3, fixture.Dimensions.Count));
        Assert.Equal(result.QualityScore, result.FixtureResults.Sum(static fixture => fixture.WeightedScore), 3);
    }

    [Fact]
    public async Task RunSingleAsync_QualityResponseIsGarbage_ReturnsLowQualityScore()
    {
        var responses = new List<LlmResponse>
        {
            new("m:test", "ready", null, true, 4, 64, TimeSpan.FromSeconds(1.0),
                LoadDuration: TimeSpan.Zero,
                PromptEvalDuration: TimeSpan.FromSeconds(0.1),
                EvalDuration: TimeSpan.FromSeconds(1.0))
        };

        foreach (var _ in ModelBenchmarkFixtures.DefaultSuite)
        {
            responses.Add(new LlmResponse("m:test", "definitely not json", null, true, 5, 5, TimeSpan.FromSeconds(0.5)));
            responses.Add(new LlmResponse("m:test", "still not json", null, true, 5, 5, TimeSpan.FromSeconds(0.5)));
        }

        var llmClient = new ScriptedLlmClient([.. responses]);
        llmClient.Running = new LlmRunningModel("m:test", "m:test", null, SizeVramBytes: 4_000_000_000, SizeBytes: 4_000_000_000);

        var probe = new OllamaCapacityProbe(llmClient, Options);
        var benchmark = new OllamaModelBenchmarkService(probe, llmClient, Options);

        var result = await benchmark.RunSingleAsync("m:test");

        Assert.True(result.Succeeded);
        Assert.Equal(0.0, result.QualityScore);
        Assert.Equal(ModelBenchmarkFixtures.DefaultSuite.Count, result.FixtureResults.Count);
        Assert.All(result.FixtureResults, fixture =>
        {
            Assert.Equal(0.0, fixture.Score);
            Assert.Contains(fixture.Notes, static note => note.Contains("not valid JSON", StringComparison.Ordinal));
        });
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
        llmClient.Running = new LlmRunningModel("m:test", "m:test", null, SizeVramBytes: 4_000_000_000, SizeBytes: 4_000_000_000);

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
        llmClient.Running = new LlmRunningModel("m:test", "m:test", null, SizeVramBytes: 4_000_000_000, SizeBytes: 4_000_000_000);

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
        public LlmRunningModel? Running { get; set; }
        public int ThrowOnCall { get; set; } = -1;

        public Task<LlmModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
        {
            var running = Running is null ? Array.Empty<LlmRunningModel>() : new[] { Running };
            return Task.FromResult(new LlmModelAvailability(
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

        public Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<LlmModelInfo?>(null);
    }
}
