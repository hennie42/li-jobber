using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Core.Documents;

/// <summary>
/// Public-safe structured snapshot of the candidate + target role data used to
/// build the document. Attached to a <see cref="GeneratedDocument"/> by the
/// renderer so the export pipeline can emit an ATS / LLM-parser-friendly
/// custom XML part inside the produced <c>.docx</c> and enrich the document's
/// core properties (keywords) without re-deriving anything from the rendered
/// markdown.
/// </summary>
/// <remarks>
/// Deliberately omits internal assessment data — fit scores, gaps, model
/// names, prompt snapshots and any LLM telemetry — because the host
/// document is shipped to recruiters and downstream parsers, not retained
/// internally.
/// </remarks>
public sealed record AtsCandidateSnapshot(
    string FullName,
    string? Headline,
    PersonalContactInfo? Contact,
    string? TargetRoleTitle,
    string? TargetCompanyName,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> MustHaveThemes,
    IReadOnlyList<AtsExperienceEntry> Experience,
    IReadOnlyList<AtsEducationEntry> Education,
    IReadOnlyList<string> Certifications,
    IReadOnlyList<LanguageProficiency> Languages);

/// <summary>Compact public-safe view of an experience entry (no descriptions).</summary>
public sealed record AtsExperienceEntry(string Title, string Company, string? Period);

/// <summary>Compact public-safe view of an education entry.</summary>
public sealed record AtsEducationEntry(string? Degree, string Institution, string? Period);
