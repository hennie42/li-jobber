using LiCvWriter.Core.Jobs;

namespace LiCvWriter.Application.Models;

public sealed record CompanyProfileBuildResult(
    CompanyResearchProfile Profile,
    int AttemptedSourceCount,
    int SuccessfulSourceCount,
    IReadOnlyList<string> SkippedSourceDetails)
{
    public int SkippedSourceCount => SkippedSourceDetails.Count;

    public bool HasSkippedSources => SkippedSourceCount > 0;
}