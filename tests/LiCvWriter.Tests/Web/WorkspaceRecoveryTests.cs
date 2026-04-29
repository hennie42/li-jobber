using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Infrastructure.LinkedIn;
using LiCvWriter.Web.Services;

namespace LiCvWriter.Tests.Web;

public sealed class WorkspaceRecoveryTests
{
    private string jobSetId = "job-set-01";

    [Fact]
    public void WorkspaceSession_RestoresAdditionalInstructionsAndLlmSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-recovery-{Guid.NewGuid():N}");

        try
        {
            var store = new WorkspaceRecoveryStore(new StorageOptions { WorkingRoot = root });
            var options = new OllamaOptions { Model = "configured-model", Think = "low" };
            var session = new WorkspaceSession(options, store);

            session.SetOllamaAvailability(new OllamaModelAvailability(
                "0.9.0",
                "configured-model",
                true,
                ["configured-model", "session-model"]));
            session.SetLlmSessionSettings("session-model", "high");
            session.SetJobSetAdditionalInstructions(jobSetId, "Use concise, evidence-backed bullets.");
            session.AddJobSet();
            jobSetId = "job-set-02";
            session.SetJobSetAdditionalInstructions(jobSetId, "Prioritize platform architecture outcomes.");

            var restoredSession = new WorkspaceSession(options, store);

            Assert.Equal("session-model", restoredSession.SelectedLlmModel);
            Assert.Equal("high", restoredSession.SelectedThinkingLevel);

            jobSetId = "job-set-01";
            Assert.Equal("Use concise, evidence-backed bullets.", restoredSession.GetJobSet(jobSetId).AdditionalInstructions);

            jobSetId = "job-set-02";
            Assert.Equal("Prioritize platform architecture outcomes.", restoredSession.GetJobSet(jobSetId).AdditionalInstructions);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSession_RestoresSharedDraftGenerationPreferences()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-recovery-{Guid.NewGuid():N}");

        try
        {
            var store = new WorkspaceRecoveryStore(new StorageOptions { WorkingRoot = root });
            var session = new WorkspaceSession(new OllamaOptions(), store);

            session.SetDraftGenerationPreferences(new DraftGenerationPreferences
            {
                GenerateCv = true,
                GenerateCoverLetter = false,
                GenerateSummary = false,
                GenerateRecommendations = true,
                GenerateInterviewNotes = true,
                ContactEmail = "alex@example.com",
                ContactPhone = "12345",
                ContactLinkedIn = "https://linkedin.com/in/alex",
                ContactCity = "Copenhagen"
            });

            var restoredSession = new WorkspaceSession(new OllamaOptions(), store);
            var preferences = restoredSession.DraftGenerationPreferences;

            Assert.True(preferences.GenerateCv);
            Assert.False(preferences.GenerateCoverLetter);
            Assert.False(preferences.GenerateSummary);
            Assert.True(preferences.GenerateRecommendations);
            Assert.True(preferences.GenerateInterviewNotes);
            Assert.Equal("alex@example.com", preferences.ContactEmail);
            Assert.Equal("12345", preferences.ContactPhone);
            Assert.Equal("https://linkedin.com/in/alex", preferences.ContactLinkedIn);
            Assert.Equal("Copenhagen", preferences.ContactCity);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSession_UsesConfiguredLlmDefaultsWhenRecoveryDoesNotContainLlmSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-recovery-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(root);
            var snapshotPath = Path.Combine(root, "workspace-recovery.json");
            File.WriteAllText(snapshotPath, """
                {
                  "activeJobSetId": "job-set-01",
                  "jobSets": [
                    {
                      "id": "job-set-01",
                      "sortOrder": 1,
                      "defaultTitle": "Job set 1",
                      "outputFolderName": "job-set-01",
                      "outputLanguage": 0,
                      "progressState": 0,
                      "progressDetail": "LLM work not started for this job set.",
                      "jobUrl": "",
                      "companyUrlsText": "",
                      "jobPosting": null,
                      "companyProfile": null,
                      "exports": []
                    }
                  ],
                  "applicantDifferentiatorProfile": null,
                  "candidateProfile": null
                }
                """);

            var store = new WorkspaceRecoveryStore(new StorageOptions { WorkingRoot = root });
            var options = new OllamaOptions { Model = "configured-model", Think = "high" };
            var restoredSession = new WorkspaceSession(options, store);

            Assert.Equal("configured-model", restoredSession.SelectedLlmModel);
            Assert.Equal("high", restoredSession.SelectedThinkingLevel);
            Assert.Equal(string.Empty, restoredSession.GetJobSet(jobSetId).AdditionalInstructions);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSession_RestoresRecoveredJobSetsWithoutProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-recovery-{Guid.NewGuid():N}");

        try
        {
            var store = new WorkspaceRecoveryStore(new StorageOptions { WorkingRoot = root });
            var session = new WorkspaceSession(new OllamaOptions(), store);
            session.AddJobSet();
            jobSetId = "job-set-02";
            session.SetJobSetJobPosting(jobSetId, new JobPostingAnalysis
            {
                RoleTitle = "Lead AI Architect",
                CompanyName = "Contoso",
                Summary = "Drive AI adoption"
            });
            session.SetJobSetOutputLanguage(jobSetId, OutputLanguage.Danish);
            session.SetJobSetGeneratedDocuments(jobSetId, 
                [new GeneratedDocument(DocumentKind.Cv, "CV", "# CV", "CV", DateTimeOffset.UtcNow)],
                [new DocumentExportResult(DocumentKind.Cv, Path.Combine(root, "Exports", "job-set-02", "cv.docx"))]);

            var restoredSession = new WorkspaceSession(new OllamaOptions(), store);

            Assert.Null(restoredSession.CandidateProfile);
            Assert.Equal(2, restoredSession.JobSets.Count);
            Assert.Equal(OutputLanguage.Danish, restoredSession.GetJobSet(jobSetId).OutputLanguage);
            Assert.Equal(JobSetProgressState.Done, restoredSession.GetJobSet(jobSetId).ProgressState);
            Assert.Single(restoredSession.GetJobSet(jobSetId).Exports);
            Assert.Single(restoredSession.GetJobSet(jobSetId).GeneratedDocuments);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSession_RestartsRunningJobSetsAsNotStarted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-recovery-{Guid.NewGuid():N}");

        try
        {
            var store = new WorkspaceRecoveryStore(new StorageOptions { WorkingRoot = root });
            var session = new WorkspaceSession(new OllamaOptions(), store);
            session.MarkJobSetRunning(jobSetId, "Generation is running for this tab.");

            var restoredSession = new WorkspaceSession(new OllamaOptions(), store);

            Assert.Equal(JobSetProgressState.NotStarted, restoredSession.GetJobSet(jobSetId).ProgressState);
            Assert.Contains("Recovered after restart", restoredSession.GetJobSet(jobSetId).ProgressDetail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSession_PersistsDeletedJobSetAndActiveSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-recovery-{Guid.NewGuid():N}");

        try
        {
            var store = new WorkspaceRecoveryStore(new StorageOptions { WorkingRoot = root });
            var session = new WorkspaceSession(new OllamaOptions(), store);
            session.AddJobSet();
            session.AddJobSet();
            session.DeleteJobSet("job-set-02");

            var restoredSession = new WorkspaceSession(new OllamaOptions(), store);

            Assert.Equal(2, restoredSession.JobSets.Count);
            Assert.DoesNotContain(restoredSession.JobSets, jobSet => jobSet.Id == "job-set-02");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSession_RestoresApplicantDifferentiatorProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-recovery-{Guid.NewGuid():N}");

        try
        {
            var store = new WorkspaceRecoveryStore(new StorageOptions { WorkingRoot = root });
            var session = new WorkspaceSession(new OllamaOptions(), store);
            session.SetApplicantDifferentiatorProfile(new ApplicantDifferentiatorProfile
            {
                TargetNarrative = "Pragmatic AI architect",
                StakeholderStyle = "Trusted advisor"
            });

            var restoredSession = new WorkspaceSession(new OllamaOptions(), store);

            Assert.Equal("Pragmatic AI architect", restoredSession.ApplicantDifferentiatorProfile.TargetNarrative);
            Assert.Equal("Trusted advisor", restoredSession.ApplicantDifferentiatorProfile.StakeholderStyle);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSession_RestoresSavedEvidenceSelectionsForJobTab()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-recovery-{Guid.NewGuid():N}");

        try
        {
            var store = new WorkspaceRecoveryStore(new StorageOptions { WorkingRoot = root });
            var session = new WorkspaceSession(new OllamaOptions(), store);
            session.SetJobSetEvidenceSelection(jobSetId, new EvidenceSelectionResult(
            [
                new RankedEvidenceItem(
                    new CandidateEvidenceItem("experience:contoso", CandidateEvidenceType.Experience, "Lead Architect @ Contoso", "Azure delivery", ["Azure"]),
                    30,
                    ["Supports must-have: Azure"],
                    true),
                new RankedEvidenceItem(
                    new CandidateEvidenceItem("skill:kubernetes", CandidateEvidenceType.Skill, "Kubernetes", "Kubernetes", ["Kubernetes"]),
                    20,
                    ["Supports must-have: Kubernetes"],
                    false)
            ]));
            session.SetJobSetEvidenceSelected(jobSetId, "skill:kubernetes", true);

            var restoredSession = new WorkspaceSession(new OllamaOptions(), store);

            Assert.Contains("experience:contoso", restoredSession.GetJobSet(jobSetId).SelectedEvidenceIds);
            Assert.Contains("skill:kubernetes", restoredSession.GetJobSet(jobSetId).SelectedEvidenceIds);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSession_RestoresInputModeAndTextFields()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-recovery-{Guid.NewGuid():N}");

        try
        {
            var store = new WorkspaceRecoveryStore(new StorageOptions { WorkingRoot = root });
            var session = new WorkspaceSession(new OllamaOptions(), store);
            session.AddJobSet(JobSetInputMode.PasteText);
            jobSetId = "job-set-02";
            session.UpdateJobSetInputs(jobSetId, string.Empty, string.Empty, "Pasted job posting content", "Pasted company info");

            var restoredSession = new WorkspaceSession(new OllamaOptions(), store);

            jobSetId = "job-set-02";
            Assert.Equal(JobSetInputMode.PasteText, restoredSession.GetJobSet(jobSetId).InputMode);
            Assert.Equal("Pasted job posting content", restoredSession.GetJobSet(jobSetId).JobPostingText);
            Assert.Equal("Pasted company info", restoredSession.GetJobSet(jobSetId).CompanyContextText);

            jobSetId = "job-set-01";
            Assert.Equal(JobSetInputMode.LinkToUrls, restoredSession.GetJobSet(jobSetId).InputMode);
            Assert.Equal(string.Empty, restoredSession.GetJobSet(jobSetId).JobPostingText);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSession_RestoresLinkedInDiagnosticsFromProfile_WhenOldRecoveryFileHasNoSnapshotField()
    {
        // Simulates an old workspace-recovery.json that was saved before the
        // LinkedInImportDiagnostics field was added. Only CandidateProfile is present;
        // the diagnostics snapshot must be reconstructed from it.
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-recovery-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(root);
            var recoveryPath = Path.Combine(root, "workspace-recovery.json");

            // Write a recovery JSON that has candidateProfile but no linkedInImportDiagnostics.
            File.WriteAllText(recoveryPath, """
                {
                  "candidateProfile": {
                                        "name": { "first": "Jordan", "last": "Blake" },
                    "headline": "Senior Engineer",
                    "summary": "Builds things.",
                    "experience": [
                      {
                        "companyName": "Acme",
                        "title": "Engineer",
                        "description": "Built systems.",
                        "location": null,
                        "period": { "startDate": { "raw": "2022", "year": 2022 }, "endDate": null }
                      }
                    ],
                    "education": [],
                    "certifications": [],
                    "projects": [],
                    "recommendations": [],
                    "manualSignals": {}
                  },
                  "linkedInAuthorizationStatus": null,
                  "linkedInImportDiagnostics": null
                }
                """);

            var store = new WorkspaceRecoveryStore(new StorageOptions { WorkingRoot = root });
            var session = new WorkspaceSession(new OllamaOptions(), store);

            // Diagnostics must be reconstructed from the profile despite being absent in the JSON.
            Assert.NotNull(session.LinkedInImportDiagnostics);
            Assert.Equal("Jordan Blake", session.LinkedInImportDiagnostics!.Profile.FullName);
            Assert.Equal("Senior Engineer", session.LinkedInImportDiagnostics.Profile.Headline);
            Assert.Equal(1, session.LinkedInImportDiagnostics.Profile.ExperienceCount);
            Assert.Single(session.LinkedInImportDiagnostics.ExperienceEntries);
            Assert.Equal("Engineer @ Acme", session.LinkedInImportDiagnostics.ExperienceEntries[0].DisplayTitle);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSession_RestoresLinkedInDiagnosticsAndAuthorizationState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-recovery-{Guid.NewGuid():N}");

        try
        {
            var store = new WorkspaceRecoveryStore(new StorageOptions { WorkingRoot = root });
            var session = new WorkspaceSession(new OllamaOptions(), store);
            var authorizedAt = new DateTimeOffset(2026, 4, 16, 18, 0, 0, TimeSpan.Zero);

            session.SetLinkedInAuthorizationStatus(new LinkedInAuthorizationStatus(
                true,
                "DMA member snapshot loaded.",
                authorizedAt,
                authorizedAt.AddHours(1),
                "r_dma_portability_self_serve"));
            session.SetImportResult(
                Path.Combine(root, "LI-export"),
                new LinkedInExportImportResult(
                    new CandidateProfile
                    {
                        Name = new PersonName("Alex", "Taylor"),
                        Headline = "Lead Architect",
                        Summary = "Builds pragmatic AI and cloud systems.",
                        Experience =
                        [
                            new ExperienceEntry(
                                "Contoso",
                                "Lead Architect",
                                "Led enterprise transformation work.",
                                null,
                                new DateRange(new PartialDate("2024", 2024)))
                        ]
                    },
                    new LinkedInExportInspection(
                        Path.Combine(root, "LI-export"),
                        ["Positions.csv", "Profile.csv"],
                        Array.Empty<string>()),
                    Array.Empty<string>(),
                    "DMA member snapshot"));

            var restoredSession = new WorkspaceSession(new OllamaOptions(), store);

            Assert.NotNull(restoredSession.LinkedInImportDiagnostics);
            Assert.Equal("DMA member snapshot", restoredSession.LinkedInImportDiagnostics!.SourceDescription);
            Assert.Equal("Alex Taylor", restoredSession.LinkedInImportDiagnostics.Profile.FullName);
            Assert.Contains("Positions.csv", restoredSession.LinkedInImportDiagnostics.DiscoveredFiles);
            Assert.True(restoredSession.LinkedInAuthorizationStatus.IsAuthorized);
            Assert.Equal("DMA member snapshot loaded.", restoredSession.LinkedInAuthorizationStatus.Message);
            Assert.Equal(authorizedAt, restoredSession.LinkedInAuthorizationStatus.AuthorizedAtUtc);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
