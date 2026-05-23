using System.Linq;

namespace LiCvWriter.Application.Models;

/// <summary>
/// Deterministic result for a single benchmark fixture executed against one model response.
/// </summary>
public sealed record ModelBenchmarkFixtureResult(
    string FixtureId,
    string PromptId,
    string DisplayName,
    double Weight,
    double Score,
    IReadOnlyList<ModelBenchmarkDimensionScore> Dimensions,
    IReadOnlyList<string> Notes)
{
    /// <summary>
    /// Gets whether every scored dimension met the fixture's success bar.
    /// </summary>
    public bool Passed => Dimensions.All(static dimension => dimension.Passed);

    /// <summary>
    /// Gets the weighted contribution of this fixture to the aggregate quality score.
    /// </summary>
    public double WeightedScore => Score * Weight;
}