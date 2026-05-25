using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using LiCvWriter.Web.Services;

namespace LiCvWriter.Tests.Web;

public sealed class WorkspaceSessionRecoveryTests
{
    [Fact]
    public void LastBenchmarkSession_RoundTripsThroughRecoveryStore()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"licv-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var storage = new StorageOptions { WorkingRoot = temp };
            var store = new WorkspaceRecoveryStore(storage);
            var ollamaOptions = new OllamaOptions();

            var session = new WorkspaceSession(ollamaOptions, store);

            var result = new ModelBenchmarkResult(
                Model: "m:test",
                Rank: 1,
                OverallScore: 0.85,
                QualityScore: 0.9,
                DecodeTokensPerSecond: 42.0,
                LoadDuration: TimeSpan.FromSeconds(0.5),
                TotalDuration: TimeSpan.FromSeconds(3.2),
                Fit: OllamaCapacityFit.Comfortable,
                Notes: ["sample note"],
                FailedReason: null,
                FixtureResults:
                [
                    new ModelBenchmarkFixtureResult(
                        FixtureId: ModelBenchmarkFixtures.FixtureId,
                        PromptId: LlmPromptCatalog.JobExtractJson,
                        DisplayName: ModelBenchmarkFixtures.FixtureDisplayName,
                        Weight: ModelBenchmarkFixtures.FixtureWeight,
                        Score: 0.9,
                        Dimensions:
                        [
                            new ModelBenchmarkDimensionScore("json-parsable", 1.0, 0.4, true, "Output parsed as JSON."),
                            new ModelBenchmarkDimensionScore("required-keys", 1.0, 0.3, true, "3/3 required keys present."),
                            new ModelBenchmarkDimensionScore("value-similarity", 1.0, 0.3, true, "Expected-value similarity 1.00.")
                        ],
                        Notes: Array.Empty<string>())]);
            var benchmarkSession = new ModelBenchmarkSession(
                StartedUtc: new DateTimeOffset(2026, 4, 22, 10, 0, 0, TimeSpan.Zero),
                CompletedUtc: new DateTimeOffset(2026, 4, 22, 10, 5, 0, TimeSpan.Zero),
                IsRunning: false,
                IsCancelled: false,
                CompletedCount: 1,
                TotalCount: 1,
                CurrentModel: null,
                Results: [result])
            {
                CurrentPhase = ModelBenchmarkRunPhase.Completed,
                CurrentDetail = "Benchmark run completed.",
                CompletedFixtureCount = 3,
                TotalFixtureCount = 3
            };

            session.SetLastBenchmarkSession(benchmarkSession);

            // Rehydrate a brand new WorkspaceSession from the same store.
            var rehydrated = new WorkspaceSession(ollamaOptions, store);

            Assert.NotNull(rehydrated.LastBenchmarkSession);
            Assert.Single(rehydrated.LastBenchmarkSession!.Results);
            var rehydratedResult = rehydrated.LastBenchmarkSession.Results[0];
            Assert.Equal("m:test", rehydratedResult.Model);
            Assert.Equal(1, rehydratedResult.Rank);
            Assert.Equal(0.85, rehydratedResult.OverallScore, 3);
            Assert.Equal(OllamaCapacityFit.Comfortable, rehydratedResult.Fit);
            Assert.Single(rehydratedResult.Notes);
            Assert.Null(rehydratedResult.FailedReason);
            Assert.Single(rehydratedResult.FixtureResults);
            Assert.Equal(ModelBenchmarkFixtures.FixtureId, rehydratedResult.FixtureResults[0].FixtureId);
            Assert.Equal(ModelBenchmarkRunPhase.Completed, rehydrated.LastBenchmarkSession.CurrentPhase);
            Assert.Equal(3, rehydrated.LastBenchmarkSession.TotalFixtureCount);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BenchmarkResultsHistory_RoundTripsThroughRecoveryStore()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"licv-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var storage = new StorageOptions { WorkingRoot = temp };
            var store = new WorkspaceRecoveryStore(storage);
            var ollamaOptions = new OllamaOptions();

            var session = new WorkspaceSession(ollamaOptions, store);

            session.SetLastBenchmarkSession(new ModelBenchmarkSession(
                StartedUtc: new DateTimeOffset(2026, 4, 22, 10, 0, 0, TimeSpan.Zero),
                CompletedUtc: new DateTimeOffset(2026, 4, 22, 10, 5, 0, TimeSpan.Zero),
                IsRunning: false,
                IsCancelled: false,
                CompletedCount: 1,
                TotalCount: 1,
                CurrentModel: null,
                Results:
                [
                    new ModelBenchmarkResult(
                        Model: "ollama-a",
                        Rank: 1,
                        OverallScore: 0.75,
                        QualityScore: 0.80,
                        DecodeTokensPerSecond: 20.0,
                        LoadDuration: null,
                        TotalDuration: TimeSpan.FromSeconds(4),
                        Fit: OllamaCapacityFit.Usable,
                        Notes: [],
                        FailedReason: null,
                        Provider: LlmProviderKind.Ollama)
                ]));

            session.SetLastBenchmarkSession(new ModelBenchmarkSession(
                StartedUtc: new DateTimeOffset(2026, 4, 22, 11, 0, 0, TimeSpan.Zero),
                CompletedUtc: new DateTimeOffset(2026, 4, 22, 11, 5, 0, TimeSpan.Zero),
                IsRunning: false,
                IsCancelled: false,
                CompletedCount: 1,
                TotalCount: 1,
                CurrentModel: null,
                Results:
                [
                    new ModelBenchmarkResult(
                        Model: "foundry-a",
                        Rank: 1,
                        OverallScore: 0.88,
                        QualityScore: 0.86,
                        DecodeTokensPerSecond: 18.0,
                        LoadDuration: null,
                        TotalDuration: TimeSpan.FromSeconds(5),
                        Fit: OllamaCapacityFit.Comfortable,
                        Notes: [],
                        FailedReason: null,
                        Provider: LlmProviderKind.Foundry)
                ],
                Provider: LlmProviderKind.Foundry));

            var rehydrated = new WorkspaceSession(ollamaOptions, store);

            Assert.Equal(2, rehydrated.BenchmarkResultsHistory.Count);
            Assert.Contains(rehydrated.BenchmarkResultsHistory, result => result.Provider == LlmProviderKind.Ollama && result.Model == "ollama-a");
            Assert.Contains(rehydrated.BenchmarkResultsHistory, result => result.Provider == LlmProviderKind.Foundry && result.Model == "foundry-a");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }
}
