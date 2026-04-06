using LiCvWriter.Application.Models;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Infrastructure.LinkedIn;

namespace LiCvWriter.Tests.LinkedIn;

public sealed class LinkedInImportDiagnosticsFormatterTests
{
    [Fact]
    public void BuildSnapshot_FormatsExperienceAndManualSignalsForDiagnostics()
    {
        var result = new LinkedInExportImportResult(
            new CandidateProfile
            {
                Name = new PersonName("Alex", "Taylor"),
                Headline = "Consultant",
                Experience =
                [
                    new ExperienceEntry(
                        "Contoso Consulting",
                        "Principal Consultant",
                        "Led delivery\nShaped architecture",
                        "Remote",
                        new DateRange(new PartialDate("Jan 2024", 2024, 1)))
                ],
                ManualSignals = new Dictionary<string, string>
                {
                    ["Languages"] = "English | Proficiency: Native\nFrench | Proficiency: Professional working",
                    ["Courses"] = "Prompt Engineering for Developers | Provider: LinkedIn Learning"
                }
            },
            new LinkedInExportInspection(
                "LinkedIn DMA member snapshot API",
                ["Profile.csv", "Languages.csv", "Courses.csv"],
                Array.Empty<string>()),
            ["Missing expected LinkedIn export file: Positions.csv"],
            "LinkedIn DMA member snapshot API");

        var snapshot = LinkedInImportDiagnosticsFormatter.BuildSnapshot(result);

        Assert.Equal("Alex Taylor", snapshot.Profile.FullName);
        Assert.Equal(1, snapshot.Profile.ExperienceCount);
        Assert.Equal(2, snapshot.Profile.ManualSignalCount);
        Assert.Equal(["Courses.csv", "Languages.csv", "Profile.csv"], snapshot.DiscoveredFiles);

        var experience = Assert.Single(snapshot.ExperienceEntries);
        Assert.Equal("Principal Consultant @ Contoso Consulting", experience.DisplayTitle);
        Assert.Equal("Remote", experience.Location);
        Assert.Equal(["Led delivery", "Shaped architecture"], experience.DescriptionLines);

        Assert.Contains(snapshot.ManualSignalEntries, entry =>
            entry.Title == "Languages"
            && entry.Lines.Count == 2
            && entry.Lines[0].Contains("English", StringComparison.OrdinalIgnoreCase));
    }
}