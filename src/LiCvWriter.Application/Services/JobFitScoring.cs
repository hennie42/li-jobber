using LiCvWriter.Core.Jobs;

namespace LiCvWriter.Application.Services;

/// <summary>
/// Shared scoring, recommendation, and assessment-building logic for fit reviews.
/// Extracted from <see cref="JobFitAnalysisService"/> so that the LLM enhancement
/// service can rebuild a <see cref="JobFitAssessment"/> from merged requirement assessments.
/// </summary>
public static class JobFitScoring
{
    /// <summary>
    /// Builds a complete <see cref="JobFitAssessment"/> from a set of requirement assessments.
    /// </summary>
    public static JobFitAssessment BuildAssessment(
        IReadOnlyList<JobRequirementAssessment> assessments,
        bool isLlmEnhanced = false)
    {
        if (assessments.Count == 0)
        {
            return JobFitAssessment.Empty;
        }

        var overallScore = CalculateScore(assessments);
        var recommendation = DetermineRecommendation(assessments, overallScore);

        var strengths = assessments
            .Where(static assessment => assessment.Match == JobRequirementMatch.Strong)
            .Select(static assessment => $"{assessment.Requirement}: {assessment.Rationale}")
            .Take(4)
            .ToArray();

        var gaps = assessments
            .Where(static assessment => assessment.Match != JobRequirementMatch.Strong)
            .Select(static assessment => $"{assessment.Requirement}: {assessment.Rationale}")
            .Take(4)
            .ToArray();

        return new JobFitAssessment(overallScore, recommendation, assessments, strengths, gaps)
        {
            IsLlmEnhanced = isLlmEnhanced
        };
    }

    /// <summary>
    /// Calculates a weighted 0–100 score from a set of requirement assessments.
    /// </summary>
    public static int CalculateScore(IReadOnlyList<JobRequirementAssessment> assessments)
    {
        if (assessments.Count == 0)
        {
            return 0;
        }

        var possible = assessments.Sum(GetWeight);
        if (possible == 0)
        {
            return 0;
        }

        var earned = assessments.Sum(GetEarnedWeight);
        return (int)Math.Round((double)earned / possible * 100, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Determines the recommendation (Apply / Stretch / Skip) from a scored set of assessments.
    /// </summary>
    public static JobFitRecommendation DetermineRecommendation(IReadOnlyList<JobRequirementAssessment> assessments, int score)
    {
        if (assessments.Count == 0)
        {
            return JobFitRecommendation.InsufficientData;
        }

        var mustHaveRequirements = assessments
            .Where(static assessment => assessment.Importance == JobRequirementImportance.MustHave)
            .ToArray();

        var missingMustHaveCount = mustHaveRequirements.Count(static assessment => assessment.Match == JobRequirementMatch.Missing);
        var partialMustHaveCount = mustHaveRequirements.Count(static assessment => assessment.Match == JobRequirementMatch.Partial);

        if (missingMustHaveCount >= 2 || score < 40)
        {
            return JobFitRecommendation.Skip;
        }

        if (missingMustHaveCount > 0 || partialMustHaveCount > 1 || score < 75)
        {
            return JobFitRecommendation.Stretch;
        }

        return JobFitRecommendation.Apply;
    }

    internal static int GetWeight(JobRequirementAssessment assessment)
        => assessment.Importance switch
        {
            JobRequirementImportance.MustHave => 24,
            JobRequirementImportance.NiceToHave => 8,
            JobRequirementImportance.Cultural => 9,
            _ => 0
        };

    internal static int GetEarnedWeight(JobRequirementAssessment assessment)
        => assessment.Match switch
        {
            JobRequirementMatch.Strong => GetWeight(assessment),
            JobRequirementMatch.Partial => (int)Math.Round(GetWeight(assessment) * 0.45, MidpointRounding.AwayFromZero),
            _ => 0
        };
}
