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
            session.SetActiveJobSetAdditionalInstructions("Use concise, evidence-backed bullets.");
            session.AddJobSet();
            session.SetActiveJobSetAdditionalInstructions("Prioritize platform architecture outcomes.");

            var restoredSession = new WorkspaceSession(options, store);

            Assert.Equal("session-model", restoredSession.SelectedLlmModel);
            Assert.Equal("high", restoredSession.SelectedThinkingLevel);

            restoredSession.SelectJobSet("job-set-01");
            Assert.Equal("Use concise, evidence-backed bullets.", restoredSession.ActiveJobSet.AdditionalInstructions);

            restoredSession.SelectJobSet("job-set-02");
            Assert.Equal("Prioritize platform architecture outcomes.", restoredSession.ActiveJobSet.AdditionalInstructions);
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
            Assert.Equal(string.Empty, restoredSession.ActiveJobSet.AdditionalInstructions);
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
            session.SetJobPosting(new JobPostingAnalysis
            {
                RoleTitle = "Lead AI Architect",
                CompanyName = "Contoso",
                Summary = "Drive AI adoption"
            });
            session.SetActiveJobSetOutputLanguage(OutputLanguage.Danish);
            session.SetGeneratedDocuments(
                [new GeneratedDocument(DocumentKind.Cv, "CV", "# CV", "CV", DateTimeOffset.UtcNow)],
                [new DocumentExportResult(DocumentKind.Cv, Path.Combine(root, "Exports", "job-set-02", "cv.md"))]);

            var restoredSession = new WorkspaceSession(new OllamaOptions(), store);

            Assert.Null(restoredSession.CandidateProfile);
            Assert.Equal(2, restoredSession.JobSets.Count);
            Assert.Equal("job-set-02", restoredSession.ActiveJobSetId);
            Assert.Equal(OutputLanguage.Danish, restoredSession.ActiveJobSet.OutputLanguage);
            Assert.Equal(JobSetProgressState.Done, restoredSession.ActiveJobSet.ProgressState);
            Assert.Single(restoredSession.ActiveJobSet.Exports);
            Assert.Single(restoredSession.ActiveJobSet.GeneratedDocuments);
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
            session.MarkActiveJobSetRunning("Generation is running for this tab.");

            var restoredSession = new WorkspaceSession(new OllamaOptions(), store);

            Assert.Equal(JobSetProgressState.NotStarted, restoredSession.ActiveJobSet.ProgressState);
            Assert.Contains("Recovered after restart", restoredSession.ActiveJobSet.ProgressDetail, StringComparison.OrdinalIgnoreCase);
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
            Assert.Equal("job-set-03", restoredSession.ActiveJobSetId);
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
            session.SetActiveJobSetEvidenceSelection(new EvidenceSelectionResult(
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
            session.SetActiveJobSetEvidenceSelected("skill:kubernetes", true);

            var restoredSession = new WorkspaceSession(new OllamaOptions(), store);

            Assert.Contains("experience:contoso", restoredSession.ActiveJobSet.SelectedEvidenceIds);
            Assert.Contains("skill:kubernetes", restoredSession.ActiveJobSet.SelectedEvidenceIds);
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
            session.UpdateActiveJobSetInputs(string.Empty, string.Empty, "Pasted job posting content", "Pasted company info");

            var restoredSession = new WorkspaceSession(new OllamaOptions(), store);

            restoredSession.SelectJobSet("job-set-02");
            Assert.Equal(JobSetInputMode.PasteText, restoredSession.ActiveJobSet.InputMode);
            Assert.Equal("Pasted job posting content", restoredSession.ActiveJobSet.JobPostingText);
            Assert.Equal("Pasted company info", restoredSession.ActiveJobSet.CompanyContextText);

            restoredSession.SelectJobSet("job-set-01");
            Assert.Equal(JobSetInputMode.LinkToUrls, restoredSession.ActiveJobSet.InputMode);
            Assert.Equal(string.Empty, restoredSession.ActiveJobSet.JobPostingText);
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
                    "name": { "firstName": "Jordan", "lastName": "Blake" },
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
            Assert.Equal("Engineer @ Acme", session.LinkedInImportDiagnostics.ExperienceEntries[0].Title);
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