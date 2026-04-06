namespace LiCvWriter.Web.Services;

public sealed record TechnologyGapAssessment(
    IReadOnlyList<string> DetectedTechnologies,
    IReadOnlyList<string> PossiblyUnderrepresentedTechnologies)
{
    public static TechnologyGapAssessment Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>());

    public bool HasSignals => DetectedTechnologies.Count > 0;

    public bool HasGaps => PossiblyUnderrepresentedTechnologies.Count > 0;
}