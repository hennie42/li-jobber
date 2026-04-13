namespace LiCvWriter.Core.Jobs;

public enum JobRequirementImportance
{
    MustHave,
    NiceToHave,
    Cultural
}

public enum JobRequirementMatch
{
    Strong,
    Partial,
    Missing
}

public enum JobFitRecommendation
{
    Apply,
    Stretch,
    Skip,
    InsufficientData
}

public sealed record JobRequirementAssessment(
    string Category,
    string Requirement,
    JobRequirementImportance Importance,
    JobRequirementMatch Match,
    IReadOnlyList<string> SupportingEvidence,
    string Rationale,
    string? SourceLabel = null,
    string? SourceSnippet = null,
    int? SourceConfidence = null)
{
    /// <summary>Whether this requirement assessment was upgraded by the LLM enhancement pass.</summary>
    public bool IsLlmEnhanced { get; init; }

    public bool HasSourceContext => !string.IsNullOrWhiteSpace(SourceLabel) || !string.IsNullOrWhiteSpace(SourceSnippet) || SourceConfidence is not null;
}

public sealed record JobFitAssessment(
    int OverallScore,
    JobFitRecommendation Recommendation,
    IReadOnlyList<JobRequirementAssessment> Requirements,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Gaps)
{
    /// <summary>Whether this assessment includes results from the LLM enhancement pass.</summary>
    public bool IsLlmEnhanced { get; init; }

    public static JobFitAssessment Empty { get; } = new(
        0,
        JobFitRecommendation.InsufficientData,
        Array.Empty<JobRequirementAssessment>(),
        Array.Empty<string>(),
        Array.Empty<string>());

    public bool HasSignals => Requirements.Count > 0;

    public int MustHaveCount => Requirements.Count(static requirement => requirement.Importance == JobRequirementImportance.MustHave);

    public int StrongMatchCount => Requirements.Count(static requirement => requirement.Match == JobRequirementMatch.Strong);

    public int GapCount => Requirements.Count(static requirement => requirement.Match == JobRequirementMatch.Missing);
}