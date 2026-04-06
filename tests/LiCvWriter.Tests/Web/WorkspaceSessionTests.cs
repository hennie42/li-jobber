using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Web.Services;

namespace LiCvWriter.Tests.Web;

public sealed class WorkspaceSessionTests
{
    [Fact]
    public void SetLlmSessionSettings_MarksSessionReadyForKnownModel()
    {
        var session = new WorkspaceSession(new OllamaOptions { Model = "configured-model", Think = "low" });

        session.SetOllamaAvailability(new OllamaModelAvailability(
            "0.9.0",
            "configured-model",
            true,
            ["configured-model", "session-model"]));

        session.SetLlmSessionSettings("session-model", "high");

        Assert.True(session.IsLlmSessionConfigured);
        Assert.True(session.IsLlmReady);
        Assert.Equal("session-model", session.SelectedLlmModel);
        Assert.Equal("high", session.SelectedThinkingLevel);
    }

    [Fact]
    public void MarkLlmWorkStarted_LocksFurtherChanges()
    {
        var session = new WorkspaceSession(new OllamaOptions { Model = "configured-model", Think = "medium" });

        session.SetOllamaAvailability(new OllamaModelAvailability(
            "0.9.0",
            "configured-model",
            true,
            ["configured-model", "session-model"]));
        session.SetLlmSessionSettings("configured-model", "medium");
        session.MarkLlmWorkStarted();

        var exception = Assert.Throws<InvalidOperationException>(() => session.SetLlmSessionSettings("session-model", "high"));

        Assert.False(session.CanEditLlmSessionSettings);
        Assert.Contains("locked", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddJobSet_CreatesNewActiveTab()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.AddJobSet();

        Assert.Equal(2, session.JobSets.Count);
        Assert.Equal("job-set-02", session.ActiveJobSetId);
        Assert.Equal("Job set 2", session.ActiveJobSet.Title);
        Assert.Equal("job-set-02", session.ActiveJobSet.OutputFolderName);
    }

    [Fact]
    public void DeleteJobSet_RemovesActiveTabAndKeepsRemainingWorkspaceAvailable()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.AddJobSet();
        session.AddJobSet();
        session.DeleteJobSet("job-set-03");

        Assert.Equal(2, session.JobSets.Count);
        Assert.Equal("job-set-02", session.ActiveJobSetId);
        Assert.DoesNotContain(session.JobSets, jobSet => jobSet.Id == "job-set-03");
    }

    [Fact]
    public void AddJobSet_AfterDeletion_UsesNextAvailableIdentifier()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.AddJobSet();
        session.AddJobSet();
        session.DeleteJobSet("job-set-02");

        session.AddJobSet();

        Assert.Equal(3, session.JobSets.Count);
        Assert.Equal("job-set-04", session.ActiveJobSetId);
        Assert.Contains(session.JobSets, jobSet => jobSet.Id == "job-set-04");
    }

    [Fact]
    public void DeleteJobSet_WhenOnlyOneTabExists_Throws()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        var exception = Assert.Throws<InvalidOperationException>(() => session.DeleteJobSet("job-set-01"));

        Assert.Contains("At least one job set", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetJobPosting_UpdatesActiveJobSetFolderAndKeepsTabsIndependent()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.SetJobPosting(new JobPostingAnalysis
        {
            RoleTitle = "Lead Architect",
            CompanyName = "Contoso",
            SourceUrl = new Uri("https://example.test/job-1")
        });
        session.AddJobSet();
        session.SetJobPosting(new JobPostingAnalysis
        {
            RoleTitle = "AI Consultant",
            CompanyName = "Fabrikam",
            SourceUrl = new Uri("https://example.test/job-2")
        });
        session.SelectJobSet("job-set-01");

        Assert.Equal("Lead Architect @ Contoso", session.ActiveJobSet.Title);
        Assert.Equal("job-set-01-contoso-lead-architect", session.ActiveJobSet.OutputFolderName);
        Assert.Equal("https://example.test/job-1", session.ActiveJobSet.JobUrl);

        session.SelectJobSet("job-set-02");

        Assert.Equal("AI Consultant @ Fabrikam", session.ActiveJobSet.Title);
        Assert.Equal("job-set-02-fabrikam-ai-consultant", session.ActiveJobSet.OutputFolderName);
        Assert.Equal("https://example.test/job-2", session.ActiveJobSet.JobUrl);
    }

    [Fact]
    public void SetGeneratedDocuments_MarksActiveJobSetDone()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.SetGeneratedDocuments(
            [new GeneratedDocument(DocumentKind.Cv, "CV", "# CV", "CV", DateTimeOffset.UtcNow)],
            [new DocumentExportResult(DocumentKind.Cv, "c:/exports/cv.md")]);

        Assert.Equal(JobSetProgressState.Done, session.ActiveJobSet.ProgressState);
        Assert.Equal("Markdown drafts generated for this job set.", session.ActiveJobSet.ProgressDetail);
    }

    [Fact]
    public void SetActiveJobSetOutputLanguage_UpdatesOnlyCurrentJobSet()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.SetActiveJobSetOutputLanguage(OutputLanguage.Danish);
        session.AddJobSet();

        Assert.Equal(OutputLanguage.English, session.ActiveJobSet.OutputLanguage);

        session.SelectJobSet("job-set-01");

        Assert.Equal(OutputLanguage.Danish, session.ActiveJobSet.OutputLanguage);
    }

    [Fact]
    public void SetJobPosting_WithProfile_ClearsTechnologyGapUntilRefreshed()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.SetImportResult(
            string.Empty,
            new LinkedInExportImportResult(
                new CandidateProfile
                {
                    Name = new PersonName("Alex", "Taylor"),
                    Summary = "Experienced with .NET and Azure delivery.",
                    Skills = [new SkillTag("Azure", 1), new SkillTag(".NET", 2)]
                },
                new LinkedInExportInspection(string.Empty, Array.Empty<string>(), Array.Empty<string>()),
                Array.Empty<string>(),
                "LinkedIn API"));

        session.SetJobPosting(new JobPostingAnalysis
        {
            RoleTitle = "Lead AI Architect",
            CompanyName = "Contoso",
            Summary = "Drive generative AI, LLM, and Kubernetes platform work."
        });

        Assert.Same(TechnologyGapAssessment.Empty, session.ActiveJobSet.TechnologyGapAssessment);
    }

    [Fact]
    public void SetActiveJobSetTechnologyGapAssessment_UpdatesCurrentTab()
    {
        var session = new WorkspaceSession(new OllamaOptions());
        var assessment = new TechnologyGapAssessment(["Generative AI"], ["LLMs", "Kubernetes"]);

        session.SetActiveJobSetTechnologyGapAssessment(assessment);

        Assert.Equal(assessment, session.ActiveJobSet.TechnologyGapAssessment);
    }

    [Fact]
    public void SetApplicantDifferentiatorProfile_ClearsFitAndEvidenceSelections()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.SetActiveJobSetJobFitAssessment(new JobFitAssessment(
            72,
            JobFitRecommendation.Stretch,
            [new JobRequirementAssessment("Must have", "Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Partial, Array.Empty<string>(), "Partial evidence")],
            Array.Empty<string>(),
            Array.Empty<string>()));
        session.SetActiveJobSetEvidenceSelection(new EvidenceSelectionResult(
        [
            new RankedEvidenceItem(
                new CandidateEvidenceItem("skill:azure", CandidateEvidenceType.Skill, "Azure", "Azure", ["Azure"]),
                20,
                ["Supports must-have: Azure"],
                true)
        ]));

        session.SetApplicantDifferentiatorProfile(new ApplicantDifferentiatorProfile
        {
            TargetNarrative = "Trusted pragmatic architect"
        });

        Assert.True(session.ApplicantDifferentiatorProfile.HasContent);
        Assert.Same(JobFitAssessment.Empty, session.JobFitAssessment);
        Assert.Same(EvidenceSelectionResult.Empty, session.EvidenceSelection);
        Assert.Single(session.ActiveJobSet.SelectedEvidenceIds);
    }

    [Fact]
    public void SetActiveJobSetEvidenceSelection_ReappliesSavedSelectionsOnRefresh()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.SetActiveJobSetEvidenceSelection(new EvidenceSelectionResult(
        [
            new RankedEvidenceItem(
                new CandidateEvidenceItem("experience:contoso", CandidateEvidenceType.Experience, "Lead Architect @ Contoso", "Azure delivery", ["Azure"]),
                30,
                ["Supports must-have: Azure"],
                true),
            new RankedEvidenceItem(
                new CandidateEvidenceItem("skill:kubernetes", CandidateEvidenceType.Skill, "Kubernetes", "Kubernetes", ["Kubernetes"]),
                22,
                ["Supports must-have: Kubernetes"],
                false)
        ]));

        session.SetActiveJobSetEvidenceSelected("skill:kubernetes", true);

        session.SetActiveJobSetEvidenceSelection(new EvidenceSelectionResult(
        [
            new RankedEvidenceItem(
                new CandidateEvidenceItem("experience:contoso", CandidateEvidenceType.Experience, "Lead Architect @ Contoso", "Azure delivery", ["Azure"]),
                44,
                ["Supports must-have: Azure"],
                false),
            new RankedEvidenceItem(
                new CandidateEvidenceItem("skill:kubernetes", CandidateEvidenceType.Skill, "Kubernetes", "Kubernetes", ["Kubernetes"]),
                31,
                ["Supports must-have: Kubernetes"],
                false),
            new RankedEvidenceItem(
                new CandidateEvidenceItem("project:rag", CandidateEvidenceType.Project, "RAG prototype", "Prototype", ["RAG"]),
                29,
                ["Supports nice-to-have: RAG"],
                true)
        ]));

        Assert.Contains(session.EvidenceSelection.SelectedEvidence, item => item.Evidence.Id == "experience:contoso");
        Assert.Contains(session.EvidenceSelection.SelectedEvidence, item => item.Evidence.Id == "skill:kubernetes");
        Assert.DoesNotContain(session.EvidenceSelection.SelectedEvidence, item => item.Evidence.Id == "project:rag");
    }

    [Fact]
    public void ClearActiveJobSetEvidenceSelections_RemovesSavedSelections()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.SetActiveJobSetEvidenceSelection(new EvidenceSelectionResult(
        [
            new RankedEvidenceItem(
                new CandidateEvidenceItem("experience:contoso", CandidateEvidenceType.Experience, "Lead Architect @ Contoso", "Azure delivery", ["Azure"]),
                30,
                ["Supports must-have: Azure"],
                true)
        ]));

        session.ClearActiveJobSetEvidenceSelections();

        Assert.Empty(session.ActiveJobSet.SelectedEvidenceIds);
        Assert.Empty(session.EvidenceSelection.SelectedEvidence);
    }
}