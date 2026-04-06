namespace LiCvWriter.Core.Profiles;

public enum CandidateEvidenceType
{
    Experience,
    Project,
    Recommendation,
    Certification,
    Skill,
    Headline,
    Summary,
    Note
}

public sealed record CandidateEvidenceItem(
    string Id,
    CandidateEvidenceType Type,
    string Title,
    string Summary,
    IReadOnlyList<string> Tags,
    string? SourceReference = null);

public sealed record RankedEvidenceItem(
    CandidateEvidenceItem Evidence,
    int Score,
    IReadOnlyList<string> Reasons,
    bool IsSelected);

public sealed record EvidenceSelectionResult(IReadOnlyList<RankedEvidenceItem> RankedEvidence)
{
    public static EvidenceSelectionResult Empty { get; } = new(Array.Empty<RankedEvidenceItem>());

    public bool HasSignals => RankedEvidence.Count > 0;

    public IReadOnlyList<RankedEvidenceItem> SelectedEvidence => RankedEvidence
        .Where(static evidence => evidence.IsSelected)
        .ToArray();
}