using LiCvWriter.Application.Services;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Tests.Application;

public sealed class CandidateProfileMergeServiceTests
{
    [Fact]
    public void Merge_PrefersLiveScalarValuesAndPreservesOrderedUnion()
    {
        var service = new CandidateProfileMergeService();
        var csv = new CandidateProfile
        {
            Headline = "CSV headline",
            Experience =
            [
                new ExperienceEntry("Company A", "Architect", null, null, new DateRange(new PartialDate("2020", 2020)))
            ],
            Skills =
            [
                new SkillTag("Architecture", 1)
            ]
        };
        var live = new CandidateProfile
        {
            Headline = "Live headline",
            Skills =
            [
                new SkillTag("Architecture", 1),
                new SkillTag("AI", 2)
            ]
        };

        var result = service.Merge(csv, live, new Dictionary<string, string> { ["tone"] = "concise" });

        Assert.Equal("Live headline", result.Headline);
        Assert.Single(result.Experience);
        Assert.Equal(2, result.Skills.Count);
        Assert.Equal("concise", result.ManualSignals["tone"]);
    }

    [Fact]
    public void MergePreferPrimary_PrefersPrimaryValuesAndUsesFallbackForMissingData()
    {
        var service = new CandidateProfileMergeService();
        var primary = new CandidateProfile
        {
            Headline = "API headline",
            Skills =
            [
                new SkillTag("Architecture", 1)
            ]
        };
        var fallback = new CandidateProfile
        {
            Summary = "Fallback summary",
            Skills =
            [
                new SkillTag("Architecture", 1),
                new SkillTag("Ollama", 2)
            ],
            Experience =
            [
                new ExperienceEntry("Company A", "Architect", null, null, new DateRange(new PartialDate("2020", 2020)))
            ]
        };

        var result = service.MergePreferPrimary(primary, fallback);

        Assert.Equal("API headline", result.Headline);
        Assert.Equal("Fallback summary", result.Summary);
        Assert.Single(result.Experience);
        Assert.Equal(2, result.Skills.Count);
        Assert.Equal("Architecture", result.Skills[0].Name);
        Assert.Equal("Ollama", result.Skills[1].Name);
    }
}