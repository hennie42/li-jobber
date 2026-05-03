using LiCvWriter.Application.Services;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Tests.Application;

public sealed class JobDiscoveryProfileLightServiceTests
{
    private readonly JobDiscoveryProfileLightService service = new();

    [Fact]
    public void Build_ReturnsEmptyWhenProfileIsMissing()
    {
        var result = service.Build(candidateProfile: null, ApplicantDifferentiatorProfile.Empty);

        Assert.False(result.HasSignals);
        Assert.Equal(string.Empty, result.SearchQuery);
    }

    [Fact]
    public void Build_UsesRecentRoleSkillsAndNarrativeSignals()
    {
        var candidate = new CandidateProfile
        {
            Headline = "Lead architect for client-facing Azure delivery",
            Location = "Copenhagen",
            Industry = "Consulting",
            Experience =
            [
                new ExperienceEntry(
                    "Contoso",
                    "Lead Architect",
                    "Owned Azure delivery for enterprise clients.",
                    null,
                    new DateRange(new PartialDate("2023", 2023)))
            ],
            Skills =
            [
                new SkillTag("Azure", 1),
                new SkillTag("Kubernetes", 2),
                new SkillTag("AI", 3)
            ]
        };

        var differentiators = new ApplicantDifferentiatorProfile
        {
            TargetNarrative = "Pragmatic AI architect for complex delivery environments."
        };

        var result = service.Build(candidate, differentiators);

        Assert.True(result.HasSignals);
        Assert.Equal("Lead Architect", result.PrimaryRole);
        Assert.Equal("Copenhagen", result.PreferredLocation);
        Assert.Equal("Consulting", result.Industry);
        Assert.Contains("Azure", result.SkillKeywords);
        Assert.Contains("Kubernetes", result.SkillKeywords);
        Assert.Equal("Pragmatic AI architect for complex delivery environments.", result.TargetNarrative);
        Assert.Equal("Lead Architect Azure Kubernetes AI", result.SearchQuery);
    }
}