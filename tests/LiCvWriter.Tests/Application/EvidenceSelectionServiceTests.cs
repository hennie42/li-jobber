using LiCvWriter.Application.Services;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Tests.Application;

public sealed class EvidenceSelectionServiceTests
{
    [Fact]
    public void Build_SelectsHighValueEvidenceForTheTargetRole()
    {
        var candidate = new CandidateProfile
        {
            Summary = "Architect focused on Azure delivery and practical AI adoption.",
            Experience =
            [
                new ExperienceEntry(
                    "Contoso",
                    "Lead Architect",
                    "Led Azure modernization and client-facing architecture decisions.",
                    null,
                    new DateRange(new PartialDate("2023", 2023)))
            ],
            Projects =
            [
                new ProjectEntry(
                    "RAG prototype",
                    "Built a retrieval-augmented generation prototype for consulting delivery.",
                    null,
                    new DateRange(new PartialDate("2024", 2024)))
            ],
            Recommendations =
            [
                new RecommendationEntry(
                    new PersonName("Ada", "Lovelace"),
                    "Contoso",
                    "CTO",
                    "Trusted advisor with strong stakeholder leadership.",
                    "VISIBLE",
                    new PartialDate("2024", 2024))
            ],
            Skills = [new SkillTag("Azure", 1), new SkillTag("RAG", 2)]
        };

        var jobPosting = new JobPostingAnalysis
        {
            RoleTitle = "Lead AI Architect",
            CompanyName = "Fabrikam",
            Summary = "Drive Azure and RAG delivery with client leadership.",
            MustHaveThemes = ["Azure", "RAG", "Client leadership"],
            CulturalSignals = ["Trust"]
        };

        var fitService = new JobFitAnalysisService(new CandidateEvidenceService());
        var fitAssessment = fitService.Analyze(candidate, jobPosting, companyProfile: null, ApplicantDifferentiatorProfile.Empty);
        var service = new EvidenceSelectionService(new CandidateEvidenceService());

        var result = service.Build(candidate, jobPosting, companyProfile: null, fitAssessment, new ApplicantDifferentiatorProfile
        {
            TargetNarrative = "Trusted pragmatic AI architect"
        });

        Assert.True(result.HasSignals);
        Assert.NotEmpty(result.SelectedEvidence);
        Assert.Contains(result.SelectedEvidence, item => item.Evidence.Id.StartsWith("experience:", StringComparison.Ordinal));
        Assert.Contains(result.RankedEvidence, item => item.Reasons.Any(reason => reason.Contains("must-have", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Build_DeduplicatesEvidenceWithTheSameIdentity()
    {
        var candidate = new CandidateProfile
        {
            Certifications =
            [
                new CertificationEntry("Azure Solutions Architect", "Microsoft", null, new DateRange(new PartialDate("Jan 2023", 2023, 1), null), null),
                new CertificationEntry("Azure Solutions Architect", "Microsoft", null, new DateRange(new PartialDate("Jan 2023", 2023, 1), null), null)
            ],
            Experience =
            [
                new ExperienceEntry(
                    "Contoso",
                    "Lead Architect",
                    "Led Azure modernization.",
                    null,
                    new DateRange(new PartialDate("Jan 2023", 2023, 1), new PartialDate("Dec 2024", 2024, 12))),
                new ExperienceEntry(
                    "Contoso",
                    "Lead Architect",
                    "Led Azure modernization.",
                    null,
                    new DateRange(new PartialDate("Jan 2023", 2023, 1), new PartialDate("Dec 2024", 2024, 12)))
            ]
        };

        var jobPosting = new JobPostingAnalysis
        {
            RoleTitle = "Lead Azure Architect",
            CompanyName = "Fabrikam",
            Summary = "Need Azure architecture leadership.",
            MustHaveThemes = ["Azure", "Architecture"]
        };

        var fitService = new JobFitAnalysisService(new CandidateEvidenceService());
        var fitAssessment = fitService.Analyze(candidate, jobPosting, companyProfile: null, ApplicantDifferentiatorProfile.Empty);
        var service = new EvidenceSelectionService(new CandidateEvidenceService());

        var result = service.Build(candidate, jobPosting, companyProfile: null, fitAssessment, ApplicantDifferentiatorProfile.Empty);

        Assert.Single(result.RankedEvidence, item => item.Evidence.Id == "certification:azure-solutions-architect");
        Assert.Single(result.RankedEvidence, item => item.Evidence.Id.StartsWith("experience:", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_UsesDynamicSignalAliasesForRequirementMatching()
    {
        var candidate = new CandidateProfile
        {
            Experience =
            [
                new ExperienceEntry(
                    "Contoso",
                    "Lead Architect",
                    "Owned stakeholder management and executive communication for enterprise clients.",
                    null,
                    new DateRange(new PartialDate("2023", 2023)))
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

        var fitService = new JobFitAnalysisService(new CandidateEvidenceService());
        var fitAssessment = fitService.Analyze(candidate, jobPosting, companyProfile: null, ApplicantDifferentiatorProfile.Empty);
        var service = new EvidenceSelectionService(new CandidateEvidenceService());

        var result = service.Build(candidate, jobPosting, companyProfile: null, fitAssessment, ApplicantDifferentiatorProfile.Empty);

        Assert.Contains(result.RankedEvidence, item =>
            item.Evidence.Title == "Lead Architect @ Contoso"
            && item.Reasons.Any(reason => reason.Contains("Customer advisory", StringComparison.OrdinalIgnoreCase)));
    }
}