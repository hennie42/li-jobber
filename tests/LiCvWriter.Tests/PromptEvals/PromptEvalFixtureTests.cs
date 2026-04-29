using LiCvWriter.Application.Models;

namespace LiCvWriter.Tests.PromptEvals;

public sealed class PromptEvalFixtureTests
{
    private static readonly string[] RequiredPromptIds =
    [
        LlmPromptCatalog.JobExtractJson,
        LlmPromptCatalog.CompanyExtractJson,
        LlmPromptCatalog.HiddenRequirementsJson,
        LlmPromptCatalog.FitEnhanceJson,
        LlmPromptCatalog.TechGapJson,
        LlmPromptCatalog.InsightsDifferentiatorsJson,
        LlmPromptCatalog.DraftDocumentMarkdown,
        LlmPromptCatalog.CvSectionsMarkdown,
        LlmPromptCatalog.CvRefineMarkdown,
        LlmPromptCatalog.JsonRepair
    ];

    [Fact]
    public void All_FixturesHaveUniqueIds()
    {
        var ids = PromptEvalFixtureCatalog.All.Select(static fixture => fixture.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void All_CoversEveryProductionPromptId()
    {
        var coveredPromptIds = PromptEvalFixtureCatalog.All
            .Select(static fixture => fixture.PromptId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var requiredPromptId in RequiredPromptIds)
        {
            Assert.Contains(requiredPromptId, coveredPromptIds);
        }
    }

    [Fact]
    public void All_FixturesHaveActionableAssertions()
    {
        Assert.All(PromptEvalFixtureCatalog.All, fixture =>
        {
            Assert.False(string.IsNullOrWhiteSpace(fixture.Purpose));
            Assert.False(string.IsNullOrWhiteSpace(fixture.SourceText));
            Assert.NotEmpty(fixture.ExpectedSignals);
            Assert.NotEmpty(fixture.ForbiddenOutputs);
        });
    }

    [Fact]
    public void All_IncludesAdversarialSourceBoundaryFixtures()
    {
        Assert.Contains(PromptEvalFixtureCatalog.All, fixture =>
            fixture.SourceText.Contains("Ignore previous instructions", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(PromptEvalFixtureCatalog.All, fixture =>
            fixture.SourceText.Contains("Disregard the schema", StringComparison.OrdinalIgnoreCase));
    }
}