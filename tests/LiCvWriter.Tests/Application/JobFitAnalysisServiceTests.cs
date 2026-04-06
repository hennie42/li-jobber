using LiCvWriter.Application.Services;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Tests.Application;

public sealed class JobFitAnalysisServiceTests
{
    private readonly JobFitAnalysisService service = new(new CandidateEvidenceService());

    [Fact]
    public void Analyze_ReturnsApplyForStrongEvidenceBackedProfile()
    {
        var candidate = new CandidateProfile
        {
            Headline = "Lead architect for Azure consulting delivery",
            Summary = "Client-facing architect with Azure platform delivery, architecture leadership, and practical AI experiments.",
            Experience =
            [
                new ExperienceEntry(
                    "Contoso",
                    "Lead Architect",
                    "Owned Azure architecture, stakeholder management, and generative AI proof-of-concept delivery for enterprise clients.",
                    null,
                    new DateRange(new PartialDate("2022", 2022)))
            ],
            Skills =
            [
                new SkillTag("Azure", 1),
                new SkillTag("Architecture", 2),
                new SkillTag("Kubernetes", 3)
            ]
        };

        var jobPosting = new JobPostingAnalysis
        {
            RoleTitle = "Lead AI Architect",
            CompanyName = "Fabrikam",
            Summary = "Help clients deliver AI safely.",
            MustHaveThemes = ["Azure", "Architecture", "Client leadership"],
            NiceToHaveThemes = ["Generative AI"],
            CulturalSignals = ["Trust"]
        };

        var companyProfile = new CompanyResearchProfile
        {
            Summary = "We share knowledge and work closely with clients.",
            CulturalSignals = ["Knowledge sharing"]
        };

        var differentiators = new ApplicantDifferentiatorProfile
        {
            StakeholderStyle = "Trusted facilitator for client workshops and steering groups.",
            TargetNarrative = "Pragmatic AI architect for complex delivery environments."
        };

        var result = service.Analyze(candidate, jobPosting, companyProfile, differentiators);

        Assert.True(result.HasSignals);
        Assert.Equal(JobFitRecommendation.Apply, result.Recommendation);
        Assert.True(result.OverallScore >= 70);
        Assert.Contains(result.Requirements, requirement =>
            requirement.Requirement == "Azure"
            && requirement.Match == JobRequirementMatch.Strong
            && requirement.SupportingEvidence.Contains("Lead Architect @ Contoso"));
    }

    [Fact]
    public void Analyze_ReturnsSkipWhenMustHavesAreMissing()
    {
        var candidate = new CandidateProfile
        {
            Headline = "Senior .NET engineer",
            Summary = "Builds internal platforms with C# and .NET.",
            Skills = [new SkillTag(".NET", 1), new SkillTag("C#", 2)]
        };

        var jobPosting = new JobPostingAnalysis
        {
            RoleTitle = "AI Platform Architect",
            CompanyName = "Contoso",
            Summary = "Lead an AI platform practice.",
            MustHaveThemes = ["Generative AI", "Kubernetes", "RAG"]
        };

        var result = service.Analyze(candidate, jobPosting, companyProfile: null, ApplicantDifferentiatorProfile.Empty);

        Assert.Equal(JobFitRecommendation.Skip, result.Recommendation);
        Assert.Contains(result.Requirements, requirement => requirement.Requirement == "Generative AI" && requirement.Match == JobRequirementMatch.Missing);
    }

    [Fact]
    public void Analyze_PreservesSourceBackedRequirementExplainability()
    {
        var candidate = new CandidateProfile
        {
            Headline = "Azure architect",
            Summary = "Azure consulting architect",
            Experience =
            [
                new ExperienceEntry(
                    "Contoso",
                    "Lead Architect",
                    "Owned Azure architecture for regulated client delivery.",
                    null,
                    new DateRange(new PartialDate("2022", 2022)))
            ]
        };

        var jobPosting = new JobPostingAnalysis
        {
            RoleTitle = "Lead AI Architect",
            CompanyName = "Fabrikam",
            Summary = "Lead Azure and AI architecture.",
            Signals =
            [
                new JobContextSignal(
                    "Must have",
                    "Azure",
                    JobRequirementImportance.MustHave,
                    "jobs.example.test",
                    "Must have Azure and Kubernetes experience.",
                    97)
            ]
        };

        var result = service.Analyze(candidate, jobPosting, companyProfile: null, ApplicantDifferentiatorProfile.Empty);

        var requirement = Assert.Single(result.Requirements);
        Assert.Equal("jobs.example.test", requirement.SourceLabel);
        Assert.Equal("Must have Azure and Kubernetes experience.", requirement.SourceSnippet);
        Assert.Equal(97, requirement.SourceConfidence);
    }

    [Fact]
    public void Analyze_UsesDynamicSignalAliasesForEvidenceMatching()
    {
        var candidate = new CandidateProfile
        {
            Headline = "Principal consultant",
            Experience =
            [
                new ExperienceEntry(
                    "Contoso",
                    "Lead Architect",
                    "Owned stakeholder management and executive communication for regulated client delivery.",
                    null,
                    new DateRange(new PartialDate("2022", 2022)))
            ]
        };

        var jobPosting = new JobPostingAnalysis
        {
            RoleTitle = "Client Partner",
            CompanyName = "Fabrikam",
            Summary = "Lead customer advisory and trusted delivery.",
            Signals =
            [
                new JobContextSignal(
                    "Must have",
                    "Customer advisory",
                    JobRequirementImportance.MustHave,
                    "jobs.example.test",
                    "Strong stakeholder management and executive communication skills.",
                    94,
                    ["stakeholder management", "executive communication"])
            ]
        };

        var result = service.Analyze(candidate, jobPosting, companyProfile: null, ApplicantDifferentiatorProfile.Empty);

        var requirement = Assert.Single(result.Requirements);
        Assert.Equal("Customer advisory", requirement.Requirement);
        Assert.Equal(JobRequirementMatch.Strong, requirement.Match);
        Assert.Contains("Lead Architect @ Contoso", requirement.SupportingEvidence);
    }
}