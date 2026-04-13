using LiCvWriter.Application.Services;
using LiCvWriter.Core.Jobs;

namespace LiCvWriter.Tests.Application;

public sealed class JobFitScoringTests
{
    [Fact]
    public void CalculateScore_ReturnsZeroForEmptyAssessments()
    {
        var score = JobFitScoring.CalculateScore([]);
        Assert.Equal(0, score);
    }

    [Fact]
    public void CalculateScore_Returns100WhenAllStrong()
    {
        var assessments = new[]
        {
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Strong),
            MakeAssessment("Kubernetes", JobRequirementImportance.NiceToHave, JobRequirementMatch.Strong),
            MakeAssessment("Culture fit", JobRequirementImportance.Cultural, JobRequirementMatch.Strong)
        };

        Assert.Equal(100, JobFitScoring.CalculateScore(assessments));
    }

    [Fact]
    public void CalculateScore_WeightsMatchImportance()
    {
        // One MustHave strong (24/24), one NiceToHave missing (0/8) => 24/32 ≈ 75
        var assessments = new[]
        {
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Strong),
            MakeAssessment("Go", JobRequirementImportance.NiceToHave, JobRequirementMatch.Missing)
        };

        Assert.Equal(75, JobFitScoring.CalculateScore(assessments));
    }

    [Fact]
    public void CalculateScore_PartialEarns45PercentOfWeight()
    {
        // One MustHave partial => earned = round(24*0.45) = 11, possible = 24 => 46
        var assessments = new[]
        {
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Partial)
        };

        Assert.Equal(46, JobFitScoring.CalculateScore(assessments));
    }

    [Fact]
    public void DetermineRecommendation_ReturnsInsufficientDataForEmpty()
    {
        Assert.Equal(
            JobFitRecommendation.InsufficientData,
            JobFitScoring.DetermineRecommendation([], 0));
    }

    [Fact]
    public void DetermineRecommendation_ReturnsApplyWhenScoreHighAndNoGaps()
    {
        var assessments = new[]
        {
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Strong),
            MakeAssessment("AI", JobRequirementImportance.MustHave, JobRequirementMatch.Strong)
        };

        Assert.Equal(
            JobFitRecommendation.Apply,
            JobFitScoring.DetermineRecommendation(assessments, 90));
    }

    [Fact]
    public void DetermineRecommendation_ReturnsSkipWhenTwoMustHavesMissing()
    {
        var assessments = new[]
        {
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Missing),
            MakeAssessment("AI", JobRequirementImportance.MustHave, JobRequirementMatch.Missing)
        };

        Assert.Equal(
            JobFitRecommendation.Skip,
            JobFitScoring.DetermineRecommendation(assessments, 10));
    }

    [Fact]
    public void DetermineRecommendation_ReturnsSkipWhenScoreBelow40()
    {
        var assessments = new[]
        {
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Partial)
        };

        Assert.Equal(
            JobFitRecommendation.Skip,
            JobFitScoring.DetermineRecommendation(assessments, 35));
    }

    [Fact]
    public void DetermineRecommendation_ReturnsStretchWhenOneMustHaveMissing()
    {
        var assessments = new[]
        {
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Strong),
            MakeAssessment("AI", JobRequirementImportance.MustHave, JobRequirementMatch.Missing)
        };

        Assert.Equal(
            JobFitRecommendation.Stretch,
            JobFitScoring.DetermineRecommendation(assessments, 60));
    }

    [Fact]
    public void DetermineRecommendation_ReturnsStretchWhenScoreBelow75()
    {
        var assessments = new[]
        {
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Strong),
            MakeAssessment("AI", JobRequirementImportance.NiceToHave, JobRequirementMatch.Missing)
        };

        Assert.Equal(
            JobFitRecommendation.Stretch,
            JobFitScoring.DetermineRecommendation(assessments, 70));
    }

    [Fact]
    public void BuildAssessment_ReturnsEmptyForNoAssessments()
    {
        var result = JobFitScoring.BuildAssessment([]);
        Assert.Same(JobFitAssessment.Empty, result);
    }

    [Fact]
    public void BuildAssessment_PopulatesStrengthsAndGaps()
    {
        var assessments = new[]
        {
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Strong),
            MakeAssessment("AI", JobRequirementImportance.MustHave, JobRequirementMatch.Missing)
        };

        var result = JobFitScoring.BuildAssessment(assessments);

        Assert.Single(result.Strengths);
        Assert.Contains("Azure", result.Strengths[0]);
        Assert.Single(result.Gaps);
        Assert.Contains("AI", result.Gaps[0]);
    }

    [Fact]
    public void BuildAssessment_SetsIsLlmEnhancedWhenRequested()
    {
        var assessments = new[]
        {
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Strong)
        };

        var result = JobFitScoring.BuildAssessment(assessments, isLlmEnhanced: true);

        Assert.True(result.IsLlmEnhanced);
    }

    [Fact]
    public void BuildAssessment_IsLlmEnhancedDefaultsFalse()
    {
        var assessments = new[]
        {
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Strong)
        };

        var result = JobFitScoring.BuildAssessment(assessments);

        Assert.False(result.IsLlmEnhanced);
    }

    private static JobRequirementAssessment MakeAssessment(
        string requirement,
        JobRequirementImportance importance,
        JobRequirementMatch match)
        => new("Technical", requirement, importance, match, ["Evidence"], $"Rationale for {requirement}");
}
