using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
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
                FailedReason: null);
            var benchmarkSession = new ModelBenchmarkSession(
                StartedUtc: new DateTimeOffset(2026, 4, 22, 10, 0, 0, TimeSpan.Zero),
                CompletedUtc: new DateTimeOffset(2026, 4, 22, 10, 5, 0, TimeSpan.Zero),
                IsRunning: false,
                IsCancelled: false,
                CompletedCount: 1,
                TotalCount: 1,
                CurrentModel: null,
                Results: [result]);

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
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }
}
