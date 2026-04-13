using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Web.Services;

namespace LiCvWriter.Tests.Web;

public sealed class WorkspaceRecoveryTests
{
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
}