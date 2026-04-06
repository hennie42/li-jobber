namespace LiCvWriter.Core.Profiles;

public sealed record ApplicantDifferentiatorProfile
{
    public static ApplicantDifferentiatorProfile Empty { get; } = new();

    public string? WorkStyle { get; init; }

    public string? CommunicationStyle { get; init; }

    public string? LeadershipStyle { get; init; }

    public string? StakeholderStyle { get; init; }

    public string? Motivators { get; init; }

    public string? TargetNarrative { get; init; }

    public string? Watchouts { get; init; }

    public string? AboutApplicantBasis { get; init; }

    public bool HasContent => ToSummaryLines().Count > 0;

    public IReadOnlyList<string> ToSummaryLines()
    {
        var lines = new List<string>();
        Add(lines, ApplicantDifferentiatorFieldCatalog.WorkStyle.Label, WorkStyle);
        Add(lines, ApplicantDifferentiatorFieldCatalog.CommunicationStyle.Label, CommunicationStyle);
        Add(lines, ApplicantDifferentiatorFieldCatalog.LeadershipStyle.Label, LeadershipStyle);
        Add(lines, ApplicantDifferentiatorFieldCatalog.StakeholderStyle.Label, StakeholderStyle);
        Add(lines, ApplicantDifferentiatorFieldCatalog.Motivators.Label, Motivators);
        Add(lines, ApplicantDifferentiatorFieldCatalog.TargetNarrative.Label, TargetNarrative);
        Add(lines, ApplicantDifferentiatorFieldCatalog.Watchouts.Label, Watchouts);
        Add(lines, ApplicantDifferentiatorFieldCatalog.AboutApplicantBasis.Label, AboutApplicantBasis);
        return lines;
    }

    private static void Add(ICollection<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{label}: {value.Trim()}");
        }
    }
}