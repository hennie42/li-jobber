namespace LiCvWriter.Application.Models;

/// <summary>
/// Outcome of benchmarking a single Ollama model. Combines a capacity probe
/// (decode speed, GPU fit) with a deterministic JSON-extraction quality score
/// to produce a single overall score for ranking.
/// </summary>
public sealed record ModelBenchmarkResult(
    string Model,
    int Rank,
    double OverallScore,
    double QualityScore,
    double? DecodeTokensPerSecond,
    TimeSpan? LoadDuration,
    TimeSpan? TotalDuration,
    OllamaCapacityFit Fit,
    IReadOnlyList<string> Notes,
    string? FailedReason)
{
    public bool Succeeded => FailedReason is null;
}
