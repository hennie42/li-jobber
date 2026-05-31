namespace LiCvWriter.Application.Models;

public enum LinkedInSnapshotDomainCategory
{
    ProfileBuilder,
    Enrichment,
    OptionalDiagnostics
}

public sealed record LinkedInSnapshotDomainOption(
    string Domain,
    string Label,
    string Description,
    LinkedInSnapshotDomainCategory Category,
    bool IsSelectedByDefault)
{
    public static IReadOnlyList<LinkedInSnapshotDomainOption> All { get; } =
    [
        new("PROFILE", "Profile", "Basic biographical information used as the profile anchor.", LinkedInSnapshotDomainCategory.ProfileBuilder, true),
        new("POSITIONS", "Experience", "Job roles, companies, descriptions, locations, and dates.", LinkedInSnapshotDomainCategory.ProfileBuilder, true),
        new("EDUCATION", "Education", "Schools, degrees, dates, notes, and activities.", LinkedInSnapshotDomainCategory.ProfileBuilder, true),
        new("SKILLS", "Skills", "Skills listed on the LinkedIn profile.", LinkedInSnapshotDomainCategory.ProfileBuilder, true),
        new("CERTIFICATIONS", "Certifications", "Certifications listed on the LinkedIn profile.", LinkedInSnapshotDomainCategory.ProfileBuilder, true),
        new("PROJECTS", "Projects", "Projects listed on the LinkedIn profile.", LinkedInSnapshotDomainCategory.ProfileBuilder, true),
        new("RECOMMENDATIONS", "Recommendations", "Recommendations received and given by the member; only received/incoming rows are imported.", LinkedInSnapshotDomainCategory.ProfileBuilder, true),

        new("VOLUNTEERING_EXPERIENCES", "Volunteering", "Volunteering experience preserved as enrichment notes.", LinkedInSnapshotDomainCategory.Enrichment, true),
        new("LANGUAGES", "Languages", "Languages and proficiency levels preserved as enrichment notes.", LinkedInSnapshotDomainCategory.Enrichment, true),
        new("PUBLICATIONS", "Publications", "Publications preserved as enrichment notes.", LinkedInSnapshotDomainCategory.Enrichment, true),
        new("PATENTS", "Patents", "Patents preserved as enrichment notes.", LinkedInSnapshotDomainCategory.Enrichment, true),
        new("HONORS", "Honors", "Honors and awards preserved as enrichment notes.", LinkedInSnapshotDomainCategory.Enrichment, true),
        new("COURSES", "Courses", "Courses preserved as enrichment notes.", LinkedInSnapshotDomainCategory.Enrichment, true),
        new("ORGANIZATIONS", "Organizations", "Organizations preserved as enrichment notes.", LinkedInSnapshotDomainCategory.Enrichment, true),

        new("ENDORSEMENTS", "Endorsements", "Given and received endorsements; retained for diagnostics only.", LinkedInSnapshotDomainCategory.OptionalDiagnostics, false),
        new("CAUSES_YOU_CARE_ABOUT", "Causes", "Causes listed on the profile; retained for diagnostics only.", LinkedInSnapshotDomainCategory.OptionalDiagnostics, false),
        new("PROFILE_SUMMARY", "AI profile summary", "LinkedIn-generated profile summary; retained for diagnostics only.", LinkedInSnapshotDomainCategory.OptionalDiagnostics, false)
    ];

    public static IReadOnlyList<string> DefaultDomains { get; } = All
        .Where(static option => option.IsSelectedByDefault)
        .Select(static option => option.Domain)
        .ToArray();

    public static IReadOnlyList<string> NormalizeDomains(IEnumerable<string>? domains)
    {
        var knownDomains = All.ToDictionary(static option => option.Domain, static option => option.Domain, StringComparer.OrdinalIgnoreCase);
        var normalized = (domains ?? DefaultDomains)
            .Select(static domain => domain.Trim())
            .Where(static domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => knownDomains.TryGetValue(domain, out var knownDomain) ? knownDomain : domain.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length == 0 ? DefaultDomains : normalized;
    }
}