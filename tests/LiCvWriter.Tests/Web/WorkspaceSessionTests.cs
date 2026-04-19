using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Web.Services;

namespace LiCvWriter.Tests.Web;

public sealed class WorkspaceSessionTests
{
    private string jobSetId = "job-set-01";

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
    public void SetOllamaAvailability_AutoConfirmsWhenConfiguredModelMatches()
    {
        var session = new WorkspaceSession(new OllamaOptions { Model = "configured-model", Think = "medium" });

        session.SetOllamaAvailability(new OllamaModelAvailability(
            "0.9.0",
            "configured-model",
            true,
            ["configured-model", "other-model"]));

        Assert.True(session.IsLlmSessionConfigured);
        Assert.True(session.IsLlmReady);
        Assert.Equal("configured-model", session.SelectedLlmModel);
    }

    [Fact]
    public void SetLlmSessionSettings_AfterLlmWorkStarts_UpdatesSettings()
    {
        var session = new WorkspaceSession(new OllamaOptions { Model = "configured-model", Think = "medium" });

        session.SetOllamaAvailability(new OllamaModelAvailability(
            "0.9.0",
            "configured-model",
            true,
            ["configured-model", "session-model"]));
        session.SetLlmSessionSettings("configured-model", "medium");
        session.MarkLlmWorkStarted();

        session.SetLlmSessionSettings("session-model", "high");

        Assert.True(session.CanEditLlmSessionSettings);
        Assert.Equal("session-model", session.SelectedLlmModel);
        Assert.Equal("high", session.SelectedThinkingLevel);
    }

    [Fact]
    public void SetOllamaAvailability_AfterLlmWorkStarts_AutoConfirmsWhenConfiguredModelMatches()
    {
        var session = new WorkspaceSession(new OllamaOptions { Model = "configured-model", Think = "medium" });

        session.MarkLlmWorkStarted();
        session.SetOllamaAvailability(new OllamaModelAvailability(
            "0.9.0",
            "configured-model",
            true,
            ["configured-model", "other-model"]));

        Assert.True(session.CanEditLlmSessionSettings);
        Assert.True(session.IsLlmSessionConfigured);
        Assert.True(session.IsLlmReady);
        Assert.Equal("configured-model", session.SelectedLlmModel);
    }

    [Fact]
    public void AddJobSet_CreatesNewJobSet()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.AddJobSet();

        Assert.Equal(2, session.JobSets.Count);
        Assert.Equal("job-set-02", session.JobSets[1].Id);
        Assert.Equal("Job set 2", session.JobSets[1].Title);
        Assert.Equal("job-set-02", session.JobSets[1].OutputFolderName);
    }

    [Fact]
    public void AddJobSet_AppendsWithoutMutatingExistingJobSets()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.AddJobSet();

        Assert.Equal(2, session.JobSets.Count);
        Assert.Equal("job-set-01", session.JobSets[0].Id);
        Assert.Equal("job-set-02", session.JobSets[1].Id);
    }

    [Fact]
    public void DeleteJobSet_RemovesJobSetAndKeepsRemainingWorkspaceAvailable()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.AddJobSet();
        session.AddJobSet();
        session.DeleteJobSet("job-set-03");

        Assert.Equal(2, session.JobSets.Count);
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

        session.SetJobSetJobPosting(jobSetId, new JobPostingAnalysis
        {
            RoleTitle = "Lead Architect",
            CompanyName = "Contoso",
            SourceUrl = new Uri("https://example.test/job-1")
        });
        session.AddJobSet();
        jobSetId = "job-set-02";
        session.SetJobSetJobPosting(jobSetId, new JobPostingAnalysis
        {
            RoleTitle = "AI Consultant",
            CompanyName = "Fabrikam",
            SourceUrl = new Uri("https://example.test/job-2")
        });
        jobSetId = "job-set-01";

        Assert.Equal("Lead Architect @ Contoso", session.GetJobSet(jobSetId).Title);
        Assert.Equal("job-set-01-contoso-lead-architect", session.GetJobSet(jobSetId).OutputFolderName);
        Assert.Equal("https://example.test/job-1", session.GetJobSet(jobSetId).JobUrl);

        jobSetId = "job-set-02";

        Assert.Equal("AI Consultant @ Fabrikam", session.GetJobSet(jobSetId).Title);
        Assert.Equal("job-set-02-fabrikam-ai-consultant", session.GetJobSet(jobSetId).OutputFolderName);
        Assert.Equal("https://example.test/job-2", session.GetJobSet(jobSetId).JobUrl);
    }

    [Fact]
    public void SetGeneratedDocuments_MarksActiveJobSetDone()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.SetJobSetGeneratedDocuments(jobSetId, 
            [new GeneratedDocument(DocumentKind.Cv, "CV", "# CV", "CV", DateTimeOffset.UtcNow)],
            [new DocumentExportResult(DocumentKind.Cv, "c:/exports/cv.docx")]);

        Assert.Equal(JobSetProgressState.Done, session.GetJobSet(jobSetId).ProgressState);
        Assert.Equal("Markdown drafts generated for this job set.", session.GetJobSet(jobSetId).ProgressDetail);
    }

    [Fact]
    public void SetActiveJobSetOutputLanguage_UpdatesOnlyCurrentJobSet()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.SetJobSetOutputLanguage(jobSetId, OutputLanguage.Danish);
        session.AddJobSet();

        Assert.Equal(OutputLanguage.English, session.GetJobSet("job-set-02").OutputLanguage);

        jobSetId = "job-set-01";

        Assert.Equal(OutputLanguage.Danish, session.GetJobSet(jobSetId).OutputLanguage);
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

        session.SetJobSetJobPosting(jobSetId, new JobPostingAnalysis
        {
            RoleTitle = "Lead AI Architect",
            CompanyName = "Contoso",
            Summary = "Drive generative AI, LLM, and Kubernetes platform work."
        });

        Assert.Same(TechnologyGapAssessment.Empty, session.GetJobSet(jobSetId).TechnologyGapAssessment);
    }

    [Fact]
    public void SetActiveJobSetTechnologyGapAssessment_UpdatesCurrentTab()
    {
        var session = new WorkspaceSession(new OllamaOptions());
        var assessment = new TechnologyGapAssessment(["Generative AI"], ["LLMs", "Kubernetes"]);

        session.SetJobSetTechnologyGapAssessment(jobSetId, assessment);

        Assert.Equal(assessment, session.GetJobSet(jobSetId).TechnologyGapAssessment);
    }

    [Fact]
    public void SetApplicantDifferentiatorProfile_ClearsFitAndEvidenceSelections()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.SetJobSetJobFitAssessment(jobSetId, new JobFitAssessment(
            72,
            JobFitRecommendation.Stretch,
            [new JobRequirementAssessment("Must have", "Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Partial, Array.Empty<string>(), "Partial evidence")],
            Array.Empty<string>(),
            Array.Empty<string>()));
        session.SetJobSetEvidenceSelection(jobSetId, new EvidenceSelectionResult(
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
        Assert.Same(JobFitAssessment.Empty, session.GetJobSet(jobSetId).JobFitAssessment);
        Assert.Same(EvidenceSelectionResult.Empty, session.GetJobSet(jobSetId).EvidenceSelection);
        Assert.Single(session.GetJobSet(jobSetId).SelectedEvidenceIds);
    }

    [Fact]
    public void UpdateCandidateProfile_ClearsFitEvidenceAndGeneratedArtifacts()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.SetImportResult(
            string.Empty,
            new LinkedInExportImportResult(
                new CandidateProfile
                {
                    Name = new PersonName("Alex", "Taylor"),
                    Summary = "Experienced with .NET and Azure delivery."
                },
                new LinkedInExportInspection(string.Empty, Array.Empty<string>(), Array.Empty<string>()),
                Array.Empty<string>(),
                "LinkedIn API"));
        session.SetJobSetJobPosting(jobSetId, new JobPostingAnalysis
        {
            RoleTitle = "Lead AI Architect",
            CompanyName = "Contoso",
            Summary = "Drive generative AI and platform work."
        });
        session.SetJobSetJobFitAssessment(jobSetId, new JobFitAssessment(
            72,
            JobFitRecommendation.Stretch,
            [new JobRequirementAssessment("Must have", "Azure", JobRequirementImportance.MustHave, JobRequirementMatch.Partial, Array.Empty<string>(), "Partial evidence")],
            Array.Empty<string>(),
            Array.Empty<string>()));
        session.SetJobSetEvidenceSelection(jobSetId, new EvidenceSelectionResult(
        [
            new RankedEvidenceItem(
                new CandidateEvidenceItem("skill:azure", CandidateEvidenceType.Skill, "Azure", "Azure", ["Azure"]),
                20,
                ["Supports must-have: Azure"],
                true)
        ]));
        session.RecordJobSetFitReviewRefresh("job-set-01", "fingerprint", includedLlmEnhancement: true);
        session.SetJobSetGeneratedDocuments(jobSetId, 
            [new GeneratedDocument(DocumentKind.Cv, "CV", "# CV", "CV", DateTimeOffset.UtcNow)],
            [new DocumentExportResult(DocumentKind.Cv, "c:/exports/cv.docx")]);

        session.UpdateCandidateProfile(new CandidateProfile
        {
            Name = new PersonName("Alex", "Taylor"),
            Summary = "Updated profile summary"
        });

        Assert.Same(JobFitAssessment.Empty, session.GetJobSet(jobSetId).JobFitAssessment);
        Assert.Same(EvidenceSelectionResult.Empty, session.GetJobSet(jobSetId).EvidenceSelection);
        Assert.Equal(JobSetProgressState.NotStarted, session.GetJobSet(jobSetId).ProgressState);
        Assert.Null(session.GetJobSet(jobSetId).LastFitReviewFingerprint);
        Assert.Empty(session.GetJobSet(jobSetId).GeneratedDocuments);
    }

    [Fact]
    public void SetActiveJobSetEvidenceSelection_ReappliesSavedSelectionsOnRefresh()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.SetJobSetEvidenceSelection(jobSetId, new EvidenceSelectionResult(
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

        session.SetJobSetEvidenceSelected(jobSetId, "skill:kubernetes", true);

        session.SetJobSetEvidenceSelection(jobSetId, new EvidenceSelectionResult(
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

        Assert.Contains(session.GetJobSet(jobSetId).EvidenceSelection.SelectedEvidence, item => item.Evidence.Id == "experience:contoso");
        Assert.Contains(session.GetJobSet(jobSetId).EvidenceSelection.SelectedEvidence, item => item.Evidence.Id == "skill:kubernetes");
        Assert.DoesNotContain(session.GetJobSet(jobSetId).EvidenceSelection.SelectedEvidence, item => item.Evidence.Id == "project:rag");
    }

    [Fact]
    public void ClearActiveJobSetEvidenceSelections_RemovesSavedSelections()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.SetJobSetEvidenceSelection(jobSetId, new EvidenceSelectionResult(
        [
            new RankedEvidenceItem(
                new CandidateEvidenceItem("experience:contoso", CandidateEvidenceType.Experience, "Lead Architect @ Contoso", "Azure delivery", ["Azure"]),
                30,
                ["Supports must-have: Azure"],
                true)
        ]));

        session.ClearJobSetEvidenceSelections(jobSetId);

        Assert.Empty(session.GetJobSet(jobSetId).SelectedEvidenceIds);
        Assert.Empty(session.GetJobSet(jobSetId).EvidenceSelection.SelectedEvidence);
    }

    [Fact]
    public void AddJobSet_WithPasteTextMode_CreatesTabWithCorrectInputMode()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.AddJobSet(JobSetInputMode.PasteText);

        Assert.Equal(2, session.JobSets.Count);
        Assert.Equal(JobSetInputMode.PasteText, session.GetJobSet("job-set-02").InputMode);
    }

    [Fact]
    public void AddJobSet_DefaultMode_IsLinkToUrls()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        Assert.Equal(JobSetInputMode.LinkToUrls, session.GetJobSet(jobSetId).InputMode);

        session.AddJobSet();

        Assert.Equal(JobSetInputMode.LinkToUrls, session.GetJobSet(jobSetId).InputMode);
    }

    [Fact]
    public void UpdateActiveJobSetInputs_PersistsTextFields()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.AddJobSet(JobSetInputMode.PasteText);
        session.UpdateJobSetInputs(jobSetId, string.Empty, string.Empty, "Full job posting text here", "Company info pasted");

        Assert.Equal("Full job posting text here", session.GetJobSet(jobSetId).JobPostingText);
        Assert.Equal("Company info pasted", session.GetJobSet(jobSetId).CompanyContextText);
    }

    [Fact]
    public void SetActiveJobSetAdditionalInstructions_KeepsTabsIndependent()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.SetJobSetAdditionalInstructions(jobSetId, "Instructions for tab 1");
        session.AddJobSet();
        jobSetId = "job-set-02";
        session.SetJobSetAdditionalInstructions(jobSetId, "Instructions for tab 2");

        jobSetId = "job-set-01";
        Assert.Equal("Instructions for tab 1", session.GetJobSet(jobSetId).AdditionalInstructions);

        jobSetId = "job-set-02";
        Assert.Equal("Instructions for tab 2", session.GetJobSet(jobSetId).AdditionalInstructions);
    }

    [Fact]
    public void AddJobSet_MixedModes_TabsKeepIndependentInputModes()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        session.AddJobSet(JobSetInputMode.PasteText);
        session.AddJobSet(JobSetInputMode.LinkToUrls);

        jobSetId = "job-set-01";
        Assert.Equal(JobSetInputMode.LinkToUrls, session.GetJobSet(jobSetId).InputMode);

        jobSetId = "job-set-02";
        Assert.Equal(JobSetInputMode.PasteText, session.GetJobSet(jobSetId).InputMode);

        jobSetId = "job-set-03";
        Assert.Equal(JobSetInputMode.LinkToUrls, session.GetJobSet(jobSetId).InputMode);
    }

    [Fact]
    public void JobSets_ReturnsSnapshotThatIsSafeAfterFurtherMutations()
    {
        var session = new WorkspaceSession(new OllamaOptions());

        var snapshot = session.JobSets;
        session.AddJobSet();

        Assert.Single(snapshot);
        Assert.Equal("job-set-01", snapshot[0].Id);
        Assert.Equal(2, session.JobSets.Count);
    }
}