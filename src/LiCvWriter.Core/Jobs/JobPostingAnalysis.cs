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
}
