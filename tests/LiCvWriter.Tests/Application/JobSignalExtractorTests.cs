using LiCvWriter.Application.Services;
using LiCvWriter.Core.Jobs;

namespace LiCvWriter.Tests.Application;

public sealed class JobSignalExtractorTests
{
    [Fact]
    public void Extract_RecognizesSofterConsultingSignalsAndCultureMarkers()
    {
        var text = """
            Requirements: strong written communication and workshop facilitation skills.
            Nice to have proposal writing experience.
            Our culture values experimentation and ownership.
            """;

        var result = JobSignalExtractor.Extract(text);

        Assert.Contains("Communication", result.MustHaveThemes);
        Assert.Contains("Workshop facilitation", result.MustHaveThemes);
        Assert.Contains("Pre-sales", result.NiceToHaveThemes);
        Assert.Contains(result.CulturalSignals, signal => signal is "Experimentation" or "Ownership");
    }

    [Fact]
    public void GetAliases_PrefersSignalAliasesWhenPresent()
    {
        var signal = new JobContextSignal(
            "Must have",
            "Customer advisory",
            JobRequirementImportance.MustHave,
            "jobs.example.test",
            "Strong stakeholder management and executive communication skills.",
            92,
            ["stakeholder management", "executive communication"]);

        var aliases = JobSignalExtractor.GetAliases(signal);

        Assert.Contains("Customer advisory", aliases);
        Assert.Contains("stakeholder management", aliases);
        Assert.Contains("executive communication", aliases);
        Assert.DoesNotContain("Customer advisory", aliases.Skip(1), StringComparer.OrdinalIgnoreCase);
    }
}