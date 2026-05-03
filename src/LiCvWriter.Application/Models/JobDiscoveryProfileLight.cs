namespace LiCvWriter.Application.Models;

public sealed record JobDiscoveryProfileLight(
    string PrimaryRole,
    string Headline,
    string PreferredLocation,
    string Industry,
    IReadOnlyList<string> RecentTitles,
    IReadOnlyList<string> SkillKeywords,
    string TargetNarrative,
    string SearchQuery)
{
    public static JobDiscoveryProfileLight Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        Array.Empty<string>(),
        Array.Empty<string>(),
        string.Empty,
        string.Empty);

    public bool HasSignals => !string.IsNullOrWhiteSpace(SearchQuery)
        || RecentTitles.Count > 0
        || SkillKeywords.Count > 0;
}