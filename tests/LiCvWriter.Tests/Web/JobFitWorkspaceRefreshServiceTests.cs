using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Web.Services;

namespace LiCvWriter.Tests.Web;

public sealed class JobFitWorkspaceRefreshServiceTests
{
    [Fact]
    public void RefreshAllJobSets_UpdatesEveryTrackedJobTab()
    {
        var session = new WorkspaceSession(new OllamaOptions());
        session.SetImportResult(
            string.Empty,
            new LinkedInExportImportResult(
                new CandidateProfile
                {
                    Name = new PersonName("Alex", "Taylor"),
                    Summary = "Architect with Azure delivery, client workshops, and pragmatic AI prototyping.",
                    Experience =
                    [
                        new ExperienceEntry("Contoso", "Lead Architect", "Led Azure programs and facilitated workshops for enterprise clients.", null, new DateRange(new PartialDate("2023", 2023)))
                    ],
                    Skills = [new SkillTag("Azure", 1), new SkillTag("Communication", 2)]
                },
                new LinkedInExportInspection(string.Empty, Array.Empty<string>(), Array.Empty<string>()),
                Array.Empty<string>(),
                "LinkedIn API"));

        session.SetJobPosting(new JobPostingAnalysis
        {
            RoleTitle = "Lead AI Architect",
            CompanyName = "Contoso",
            Summary = "Must have Azure architecture experience and written communication strength.",
            MustHaveThemes = ["Azure", "Communication"]
        });

        session.AddJobSet();
        session.SetJobPosting(new JobPostingAnalysis
        {
            RoleTitle = "Principal Consultant",
            CompanyName = "Fabrikam",
            Summary = "Must have workshop facilitation and stakeholder management.",
            MustHaveThemes = ["Workshop facilitation", "Client leadership"]
        });

        var service = new JobFitWorkspaceRefreshService(
            new JobFitAnalysisService(new CandidateEvidenceService()),
            new EvidenceSelectionService(new CandidateEvidenceService()),
            session);

        var refreshed = service.RefreshAllJobSets();

        Assert.Equal(2, refreshed);

        session.SelectJobSet("job-set-01");
        Assert.True(session.JobFitAssessment.HasSignals);
        Assert.True(session.EvidenceSelection.HasSignals);

        session.SelectJobSet("job-set-02");
        Assert.True(session.JobFitAssessment.HasSignals);
        Assert.True(session.EvidenceSelection.HasSignals);
    }

    [Fact]
    public void RefreshAllJobSets_PreservesSavedSelections()
    {
        var session = new WorkspaceSession(new OllamaOptions());
        session.SetImportResult(
            string.Empty,
            new LinkedInExportImportResult(
                new CandidateProfile
                {
                    Name = new PersonName("Alex", "Taylor"),
                    Summary = "Architect with Azure delivery and workshop facilitation.",
                    Experience =
                    [
                        new ExperienceEntry("Contoso", "Lead Architect", "Led Azure delivery and workshop facilitation.", null, new DateRange(new PartialDate("2023", 2023)))
                    ],
                    Skills = [new SkillTag("Azure", 1), new SkillTag("Workshop facilitation", 2)]
                },
                new LinkedInExportInspection(string.Empty, Array.Empty<string>(), Array.Empty<string>()),
                Array.Empty<string>(),
                "LinkedIn API"));

        session.SetJobPosting(new JobPostingAnalysis
        {
            RoleTitle = "Lead Consultant",
            CompanyName = "Contoso",
            Summary = "Must have Azure and workshop facilitation.",
            MustHaveThemes = ["Azure", "Workshop facilitation"]
        });

        var service = new JobFitWorkspaceRefreshService(
            new JobFitAnalysisService(new CandidateEvidenceService()),
            new EvidenceSelectionService(new CandidateEvidenceService()),
            session);

        service.RefreshAllJobSets();
        var evidenceId = session.EvidenceSelection.SelectedEvidence.Last().Evidence.Id;
        session.SetActiveJobSetEvidenceSelected(evidenceId, false);

        service.RefreshAllJobSets();

        Assert.DoesNotContain(session.EvidenceSelection.SelectedEvidence, item => item.Evidence.Id == evidenceId);
    }
}