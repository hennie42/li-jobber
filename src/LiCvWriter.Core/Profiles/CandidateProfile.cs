namespace LiCvWriter.Core.Profiles;

public sealed record CandidateProfile
{
    public static CandidateProfile Empty { get; } = new();

    public PersonName Name { get; init; } = PersonName.Empty;

    public string? Headline { get; init; }

    public string? Summary { get; init; }

    public string? Industry { get; init; }

    public string? Location { get; init; }

    public string? PublicProfileUrl { get; init; }

    public string? PrimaryEmail { get; init; }

    public IReadOnlyList<ExperienceEntry> Experience { get; init; } = Array.Empty<ExperienceEntry>();

    public IReadOnlyList<EducationEntry> Education { get; init; } = Array.Empty<EducationEntry>();

    public IReadOnlyList<SkillTag> Skills { get; init; } = Array.Empty<SkillTag>();

    public IReadOnlyList<CertificationEntry> Certifications { get; init; } = Array.Empty<CertificationEntry>();

    public IReadOnlyList<ProjectEntry> Projects { get; init; } = Array.Empty<ProjectEntry>();

    public IReadOnlyList<RecommendationEntry> Recommendations { get; init; } = Array.Empty<RecommendationEntry>();

    public IReadOnlyDictionary<string, string> ManualSignals { get; init; } = new Dictionary<string, string>();
}
