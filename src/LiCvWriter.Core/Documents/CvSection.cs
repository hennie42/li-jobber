namespace LiCvWriter.Core.Documents;

/// <summary>
/// Identifies a logical section of the generated CV that can be produced by a
/// dedicated LLM call. Only sections that materially benefit from LLM
/// rewriting are listed here; deterministic sections (header, fit snapshot,
/// recommendations, certifications, early career) are emitted directly by the
/// renderer without an LLM round-trip.
/// </summary>
public enum CvSection
{
    /// <summary>The 3-4 line professional profile / headline paragraph.</summary>
    ProfileSummary,

    /// <summary>Comma-separated keyword line of technologies and competencies.</summary>
    KeySkills,

    /// <summary>Achievement-focused rewrites of role descriptions.</summary>
    ExperienceHighlights,

    /// <summary>Achievement-focused rewrites of project descriptions.</summary>
    ProjectHighlights
}
