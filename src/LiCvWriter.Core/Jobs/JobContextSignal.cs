namespace LiCvWriter.Core.Jobs;

public sealed record JobContextSignal(
    string Category,
    string Requirement,
    JobRequirementImportance Importance,
    string SourceLabel,
    string SourceSnippet,
    int Confidence,
    IReadOnlyList<string>? Aliases = null)
{
    public bool HasSourceContext => !string.IsNullOrWhiteSpace(SourceLabel) || !string.IsNullOrWhiteSpace(SourceSnippet);

    public IReadOnlyList<string> EffectiveAliases { get; } = Aliases ?? Array.Empty<string>();

    public bool HasAliases => EffectiveAliases.Count > 0;
}