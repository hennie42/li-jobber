namespace LiCvWriter.Application.Models;

/// <summary>
/// Weighted score for a single measurable dimension within one benchmark fixture.
/// </summary>
public sealed record ModelBenchmarkDimensionScore(
    string Dimension,
    double Score,
    double Weight,
    bool Passed,
    string? Detail = null)
{
    /// <summary>
    /// Gets the weighted contribution of this dimension to the fixture score.
    /// </summary>
    public double WeightedScore => Score * Weight;
}