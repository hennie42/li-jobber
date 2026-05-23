namespace LiCvWriter.Application.Models;

/// <summary>
/// In-flight progress update for a single model benchmark run.
/// </summary>
public sealed record ModelBenchmarkProgress(
    string Model,
    ModelBenchmarkRunPhase Phase,
    string Detail,
    int CompletedFixtureCount,
    int TotalFixtureCount,
    int CurrentFixtureNumber = 0,
    string? CurrentFixtureId = null,
    string? CurrentFixtureDisplayName = null,
    string? CurrentPromptId = null);