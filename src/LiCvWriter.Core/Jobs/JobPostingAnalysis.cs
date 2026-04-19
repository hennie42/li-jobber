namespace LiCvWriter.Core.Jobs;

public sealed record JobPostingAnalysis
{
    public Uri? SourceUrl { get; init; }

    public string RoleTitle { get; init; } = string.Empty;

    public string CompanyName { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<string> MustHaveThemes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> NiceToHaveThemes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CulturalSignals { get; init; } = Array.Empty<string>();

    public IReadOnlyList<JobContextSignal> Signals { get; init; } = Array.Empty<JobContextSignal>();

    /// <summary>
    /// Implicit requirements inferred by the LLM that are not stated in the job posting
    /// but are commonly expected for this type of role. Weighted lower than explicit requirements.
    /// </summary>
    public IReadOnlyList<string> InferredRequirements { get; init; } = Array.Empty<string>();
}
