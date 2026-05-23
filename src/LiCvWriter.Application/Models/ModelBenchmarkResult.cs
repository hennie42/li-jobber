namespace LiCvWriter.Application.Models;

/// <summary>
/// Outcome of benchmarking a single local model. Combines a capacity probe
/// (decode speed, fit classification) with a deterministic JSON-extraction
/// quality score to produce a single overall score for ranking.
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
    string? FailedReason,
    LlmProviderKind Provider = LlmProviderKind.Ollama)
{
    public bool Succeeded => FailedReason is null;
}
