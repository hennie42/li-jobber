namespace LiCvWriter.Tests.PromptEvals;

public sealed class PromptEvalScorerTests
{
    [Fact]
    public void Score_WhenAllSignalsPresentAndForbiddenAbsent_Passes()
    {
        var fixture = PromptEvalFixtureCatalog.All[0];
        var output = string.Join(" ", fixture.ExpectedSignals);

        var score = PromptEvalScorer.Score(fixture, output);

        Assert.True(score.Passed);
        Assert.Equal(fixture.ExpectedSignals.Count, score.ExpectedSignalsFound);
        Assert.Empty(score.MissingExpectedSignals);
        Assert.Empty(score.ForbiddenOutputsFound);
    }

    [Fact]
    public void Score_WhenExpectedSignalIsMissing_Fails()
    {
        var fixture = PromptEvalFixtureCatalog.All[0];

        var score = PromptEvalScorer.Score(fixture, "Only Azure landing zones are mentioned.");

        Assert.False(score.Passed);
        Assert.NotEmpty(score.MissingExpectedSignals);
    }

    [Fact]
    public void Score_WhenForbiddenOutputIsPresent_Fails()
    {
        var fixture = PromptEvalFixtureCatalog.All[0];
        var output = string.Join(" ", fixture.ExpectedSignals.Concat([fixture.ForbiddenOutputs[0]]));

        var score = PromptEvalScorer.Score(fixture, output);

        Assert.False(score.Passed);
        Assert.Contains(fixture.ForbiddenOutputs[0], score.ForbiddenOutputsFound);
    }

    [Fact]
    public void Score_WhenSchemaInvalid_Fails()
    {
        var fixture = PromptEvalFixtureCatalog.All[0];
        var output = string.Join(" ", fixture.ExpectedSignals);

        var score = PromptEvalScorer.Score(fixture, output, schemaValid: false);

        Assert.False(score.Passed);
        Assert.False(score.SchemaValid);
    }

    [Fact]
    public void Score_WhenVisibleOnlyPolicyFails_Fails()
    {
        var fixture = PromptEvalFixtureCatalog.All[0];
        var output = string.Join(" ", fixture.ExpectedSignals);

        var score = PromptEvalScorer.Score(fixture, output, visibleOnlyCompliant: false);

        Assert.False(score.Passed);
        Assert.False(score.VisibleOnlyCompliant);
    }

    [Fact]
    public void Score_AllFixturesCanBeScoredWithSyntheticPassingOutput()
    {
        foreach (var fixture in PromptEvalFixtureCatalog.All)
        {
            var output = string.Join(" ", fixture.ExpectedSignals);

            var score = PromptEvalScorer.Score(fixture, output);

            Assert.True(score.Passed, fixture.Id);
        }
    }
}