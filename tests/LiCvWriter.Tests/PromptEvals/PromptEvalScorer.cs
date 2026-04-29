namespace LiCvWriter.Tests.PromptEvals;

public sealed record PromptEvalScore(
    string FixtureId,
    string PromptId,
    bool SchemaValid,
    bool VisibleOnlyCompliant,
    IReadOnlyList<string> MissingExpectedSignals,
    IReadOnlyList<string> ForbiddenOutputsFound)
{
    public int ExpectedSignalsFound => ExpectedSignalsTotal - MissingExpectedSignals.Count;
    public int ExpectedSignalsTotal { get; init; }
    public bool Passed => SchemaValid
        && VisibleOnlyCompliant
        && MissingExpectedSignals.Count == 0
        && ForbiddenOutputsFound.Count == 0;
}

public static class PromptEvalScorer
{
    public static PromptEvalScore Score(
        PromptEvalCase fixture,
        string output,
        bool schemaValid = true,
        bool visibleOnlyCompliant = true)
    {
        var candidateOutput = output ?? string.Empty;
        var missingExpectedSignals = fixture.ExpectedSignals
            .Where(signal => !Contains(candidateOutput, signal))
            .ToArray();
        var forbiddenOutputsFound = fixture.ForbiddenOutputs
            .Where(forbidden => Contains(candidateOutput, forbidden))
            .ToArray();

        return new PromptEvalScore(
            fixture.Id,
            fixture.PromptId,
            schemaValid,
            visibleOnlyCompliant,
            missingExpectedSignals,
            forbiddenOutputsFound)
        {
            ExpectedSignalsTotal = fixture.ExpectedSignals.Count
        };
    }

    private static bool Contains(string value, string expected)
        => value.Contains(expected, StringComparison.OrdinalIgnoreCase);
}