namespace LiCvWriter.Application.Models;

/// <summary>
/// Outcome of benchmarking a single local model. Combines a capacity probe
/// (decode speed, fit classification) with one or more deterministic fixture
/// scores to produce a single overall score for ranking.
/// </summary>
public sealed record ModelBenchmarkResult
{
    public ModelBenchmarkResult(
        string Model,
        int Rank,
        double OverallScore,
        double QualityScore,
        double? DecodeTokensPerSecond,
        TimeSpan? LoadDuration,
        TimeSpan? TotalDuration,
        OllamaCapacityFit Fit,
        IReadOnlyList<string>? Notes,
        string? FailedReason,
        LlmProviderKind Provider = LlmProviderKind.Ollama,
        IReadOnlyList<ModelBenchmarkFixtureResult>? FixtureResults = null)
    {
        this.Model = Model;
        this.Rank = Rank;
        this.OverallScore = OverallScore;
        this.QualityScore = QualityScore;
        this.DecodeTokensPerSecond = DecodeTokensPerSecond;
        this.LoadDuration = LoadDuration;
        this.TotalDuration = TotalDuration;
        this.Fit = Fit;
        this.Notes = Notes ?? Array.Empty<string>();
        this.FailedReason = FailedReason;
        this.Provider = Provider;
        this.FixtureResults = FixtureResults ?? Array.Empty<ModelBenchmarkFixtureResult>();
    }

    public string Model { get; init; }

    public int Rank { get; init; }

    public double OverallScore { get; init; }

    public double QualityScore { get; init; }

    public double? DecodeTokensPerSecond { get; init; }

    public TimeSpan? LoadDuration { get; init; }

    public TimeSpan? TotalDuration { get; init; }

    public OllamaCapacityFit Fit { get; init; }

    public IReadOnlyList<string> Notes { get; init; }

    public string? FailedReason { get; init; }

    public LlmProviderKind Provider { get; init; }

    public IReadOnlyList<ModelBenchmarkFixtureResult> FixtureResults { get; init; }

    public bool Succeeded => FailedReason is null;
}
