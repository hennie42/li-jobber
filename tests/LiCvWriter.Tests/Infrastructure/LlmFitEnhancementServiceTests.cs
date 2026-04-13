using LiCvWriter.Core.Jobs;
using LiCvWriter.Infrastructure.Workflows;
using static LiCvWriter.Infrastructure.Workflows.LlmFitEnhancementService;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class LlmFitEnhancementServiceTests
{
    // ─── Merge ──────────────────────────────────────────────────────

    [Fact]
    public void Merge_UpgradesPartialToStrong()
    {
        var baseline = BuildBaseline(
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Partial));

        var enhancements = new[]
        {
            new EnhancedRequirement("Azure", "Strong", ["Recommendation from CTO"], "CTO praises Azure expertise")
        };

        var result = LlmFitEnhancementService.Merge(baseline, enhancements);

        var requirement = Assert.Single(result.Requirements);
        Assert.Equal(JobRequirementMatch.Strong, requirement.Match);
        Assert.True(requirement.IsLlmEnhanced);
        Assert.Contains("Recommendation from CTO", requirement.SupportingEvidence);
        Assert.Equal("CTO praises Azure expertise", requirement.Rationale);
        Assert.True(result.IsLlmEnhanced);
    }

    [Fact]
    public void Merge_UpgradesMissingToPartial()
    {
        var baseline = BuildBaseline(
            MakeAssessment("Kubernetes", JobRequirementImportance.NiceToHave, JobRequirementMatch.Missing));

        var enhancements = new[]
        {
            new EnhancedRequirement("Kubernetes", "Partial", ["Container orchestration in project X"], "Indirect")
        };

        var result = LlmFitEnhancementService.Merge(baseline, enhancements);

        var requirement = Assert.Single(result.Requirements);
        Assert.Equal(JobRequirementMatch.Partial, requirement.Match);
        Assert.True(requirement.IsLlmEnhanced);
    }

    [Fact]
    public void Merge_UpgradesMissingToStrong()
    {
        var baseline = BuildBaseline(
            MakeAssessment("AI", JobRequirementImportance.MustHave, JobRequirementMatch.Missing));

        var enhancements = new[]
        {
            new EnhancedRequirement("AI", "Strong", ["Led AI POC delivery"], "Direct evidence")
        };

        var result = LlmFitEnhancementService.Merge(baseline, enhancements);

        var requirement = Assert.Single(result.Requirements);
        Assert.Equal(JobRequirementMatch.Strong, requirement.Match);
    }

    [Fact]
    public void Merge_RejectsDowngradeFromStrongToPartial()
    {
        var baseline = BuildBaseline(
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Strong));

        var enhancements = new[]
        {
            new EnhancedRequirement("Azure", "Partial", ["Weaker evidence"], "Should not apply")
        };

        var result = LlmFitEnhancementService.Merge(baseline, enhancements);

        Assert.Same(baseline, result);
    }

    [Fact]
    public void Merge_RejectsDowngradeFromPartialToMissing()
    {
        var baseline = BuildBaseline(
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Partial));

        // "Missing" is not a valid newMatch value, so ParseMatch returns null → no upgrade
        var enhancements = new[]
        {
            new EnhancedRequirement("Azure", "Missing", [], "Invalid")
        };

        var result = LlmFitEnhancementService.Merge(baseline, enhancements);

        Assert.Same(baseline, result);
    }

    [Fact]
    public void Merge_ReturnsBaselineWhenNoUpgrades()
    {
        var baseline = BuildBaseline(
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Strong));

        var result = LlmFitEnhancementService.Merge(baseline, []);

        Assert.Same(baseline, result);
    }

    [Fact]
    public void Merge_IsCaseInsensitiveOnRequirementName()
    {
        var baseline = BuildBaseline(
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Partial));

        var enhancements = new[]
        {
            new EnhancedRequirement("azure", "Strong", ["Evidence"], "Rationale")
        };

        var result = LlmFitEnhancementService.Merge(baseline, enhancements);

        var requirement = Assert.Single(result.Requirements);
        Assert.Equal(JobRequirementMatch.Strong, requirement.Match);
    }

    [Fact]
    public void Merge_UpgradesOnlyMatchedRequirements()
    {
        var baseline = BuildBaseline(
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Strong),
            MakeAssessment("AI", JobRequirementImportance.MustHave, JobRequirementMatch.Missing));

        var enhancements = new[]
        {
            new EnhancedRequirement("AI", "Partial", ["AI POC work"], "Some evidence")
        };

        var result = LlmFitEnhancementService.Merge(baseline, enhancements);

        Assert.Equal(2, result.Requirements.Count);
        Assert.Equal(JobRequirementMatch.Strong, result.Requirements[0].Match);
        Assert.False(result.Requirements[0].IsLlmEnhanced);
        Assert.Equal(JobRequirementMatch.Partial, result.Requirements[1].Match);
        Assert.True(result.Requirements[1].IsLlmEnhanced);
    }

    [Fact]
    public void Merge_RecalculatesScoreAfterUpgrade()
    {
        var baseline = BuildBaseline(
            MakeAssessment("Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Missing));

        Assert.Equal(0, baseline.OverallScore);

        var enhancements = new[]
        {
            new EnhancedRequirement("Azure", "Strong", ["Evidence"], "Direct match")
        };

        var result = LlmFitEnhancementService.Merge(baseline, enhancements);

        Assert.Equal(100, result.OverallScore);
    }

    // ─── TryParse ───────────────────────────────────────────────────

    [Fact]
    public void TryParse_ValidJson_ReturnsTrue()
    {
        var json = """
            {
              "enhancedRequirements": [
                {
                  "requirement": "Azure",
                  "newMatch": "Strong",
                  "evidence": ["CTO recommends Azure skills"],
                  "rationale": "Direct praise"
                }
              ]
            }
            """;

        Assert.True(LlmFitEnhancementService.TryParse(json, out var enhancements));
        var single = Assert.Single(enhancements);
        Assert.Equal("Azure", single.Requirement);
        Assert.Equal("Strong", single.NewMatch);
        Assert.Single(single.Evidence);
        Assert.Equal("Direct praise", single.Rationale);
    }

    [Fact]
    public void TryParse_JsonWrappedInCodeFence_ReturnsTrue()
    {
        var json = """
            ```json
            {
              "enhancedRequirements": [
                {
                  "requirement": "AI",
                  "newMatch": "Partial",
                  "evidence": ["Some evidence"],
                  "rationale": "Indirect"
                }
              ]
            }
            ```
            """;

        Assert.True(LlmFitEnhancementService.TryParse(json, out var enhancements));
        Assert.Single(enhancements);
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsFalse()
    {
        Assert.False(LlmFitEnhancementService.TryParse("{not json at all", out var enhancements));
        Assert.Empty(enhancements);
    }

    [Fact]
    public void TryParse_EmptyEnhancedList_ReturnsFalse()
    {
        var json = """{"enhancedRequirements": []}""";

        Assert.False(LlmFitEnhancementService.TryParse(json, out var enhancements));
        Assert.Empty(enhancements);
    }

    [Fact]
    public void TryParse_MissingEnhancedRequirementsProperty_ReturnsFalse()
    {
        var json = """{"results": []}""";

        Assert.False(LlmFitEnhancementService.TryParse(json, out var enhancements));
        Assert.Empty(enhancements);
    }

    [Fact]
    public void TryParse_SkipsEntriesWithMissingRequiredFields()
    {
        var json = """
            {
              "enhancedRequirements": [
                {
                  "requirement": "Azure",
                  "newMatch": "Strong",
                  "evidence": ["Evidence"],
                  "rationale": "Valid"
                },
                {
                  "newMatch": "Partial",
                  "evidence": [],
                  "rationale": "Missing requirement field"
                }
              ]
            }
            """;

        Assert.True(LlmFitEnhancementService.TryParse(json, out var enhancements));
        Assert.Single(enhancements);
        Assert.Equal("Azure", enhancements[0].Requirement);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static JobFitAssessment BuildBaseline(params JobRequirementAssessment[] assessments)
        => LiCvWriter.Application.Services.JobFitScoring.BuildAssessment(assessments);

    private static JobRequirementAssessment MakeAssessment(
        string requirement,
        JobRequirementImportance importance,
        JobRequirementMatch match)
        => new("Technical", requirement, importance, match, ["Evidence"], $"Rationale for {requirement}");
}
