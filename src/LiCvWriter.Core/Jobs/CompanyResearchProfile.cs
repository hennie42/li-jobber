namespace LiCvWriter.Core.Jobs;

public sealed record CompanyResearchProfile
{
    public string Name { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<Uri> SourceUrls { get; init; } = Array.Empty<Uri>();

    public IReadOnlyList<string> GuidingPrinciples { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CulturalSignals { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Differentiators { get; init; } = Array.Empty<string>();

    public IReadOnlyList<JobContextSignal> Signals { get; init; } = Array.Empty<JobContextSignal>();
}
