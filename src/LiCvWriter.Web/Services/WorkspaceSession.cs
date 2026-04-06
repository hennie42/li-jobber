using System.Text;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Web.Services;

public sealed class WorkspaceSession(OllamaOptions ollamaOptions, WorkspaceRecoveryStore? recoveryStore = null)
{
    private static readonly string[] SupportedThinkingLevels = ["low", "medium", "high"];
    private readonly WorkspaceRecoveryStore? recoveryStateStore = recoveryStore;
    private readonly List<JobSetSessionState> jobSets = LoadRecoveredJobSets(recoveryStore);

    public event Action? Changed;

    public string ExportPath { get; private set; } = Path.Combine(Environment.CurrentDirectory, "LI-export");

    public CandidateProfile? CandidateProfile { get; private set; }

    public LinkedInExportImportResult? ImportResult { get; private set; }

    public ApplicantDifferentiatorProfile ApplicantDifferentiatorProfile { get; private set; } = LoadApplicantDifferentiatorProfile(recoveryStore);

    public IReadOnlyList<JobSetSessionState> JobSets => jobSets;

    public string ActiveJobSetId { get; private set; } = LoadActiveJobSetId(recoveryStore);

    public JobSetSessionState ActiveJobSet => jobSets.First(jobSet => jobSet.Id == ActiveJobSetId);

    public JobPostingAnalysis? JobPosting => ActiveJobSet.JobPosting;

    public CompanyResearchProfile? CompanyProfile => ActiveJobSet.CompanyProfile;

    public JobFitAssessment JobFitAssessment => ActiveJobSet.JobFitAssessment;

    public LinkedInAuthorizationStatus LinkedInAuthorizationStatus { get; private set; } = new(false, "DMA member snapshot not loaded.", null, null, null);

    public OllamaModelAvailability? OllamaAvailability { get; private set; }

    public string SelectedLlmModel { get; private set; } = ollamaOptions.Model;

    public string SelectedThinkingLevel { get; private set; } = NormalizeThinkingLevel(ollamaOptions.Think);

    public bool IsLlmSessionConfigured { get; private set; }

    public bool HasStartedLlmWork { get; private set; }

    public IReadOnlyList<GeneratedDocument> GeneratedDocuments => ActiveJobSet.GeneratedDocuments;

    public IReadOnlyList<DocumentExportResult> Exports => ActiveJobSet.Exports;

    public EvidenceSelectionResult EvidenceSelection => ActiveJobSet.EvidenceSelection;

    public bool CanGenerate => CandidateProfile is not null && ActiveJobSet.JobPosting is not null;

    public bool IsLlmReady => OllamaAvailability is not null
        && IsLlmSessionConfigured
        && OllamaAvailability.AvailableModels.Any(model => model.Equals(SelectedLlmModel, StringComparison.OrdinalIgnoreCase));

    public bool CanEditLlmSessionSettings => !HasStartedLlmWork;

    public bool CanStartDraftGeneration => CanGenerate && IsLlmReady;

    public void SetActiveJobSetOutputLanguage(OutputLanguage outputLanguage)
    {
        UpdateActiveJobSet(jobSet => jobSet with { OutputLanguage = outputLanguage });
    }

    public void MarkActiveJobSetRunning(string detail)
    {
        UpdateActiveJobSet(jobSet => jobSet with
        {
            ProgressState = JobSetProgressState.Running,
            ProgressDetail = detail
        });
    }

    public void MarkActiveJobSetFailed(string detail)
    {
        UpdateActiveJobSet(jobSet => jobSet with
        {
            ProgressState = JobSetProgressState.Failed,
            ProgressDetail = detail
        });
    }

    public void ResetActiveJobSetProgress(string detail = "LLM work not started for this job set.")
    {
        UpdateActiveJobSet(jobSet => jobSet with
        {
            ProgressState = JobSetProgressState.NotStarted,
            ProgressDetail = detail
        });
    }

    public void SetActiveJobSetTechnologyGapAssessment(TechnologyGapAssessment technologyGapAssessment)
    {
        UpdateActiveJobSet(jobSet => jobSet with { TechnologyGapAssessment = technologyGapAssessment });
    }

    public void SetActiveJobSetJobFitAssessment(JobFitAssessment jobFitAssessment)
    {
        SetJobSetJobFitAssessment(ActiveJobSetId, jobFitAssessment);
    }

    public void SetJobSetJobFitAssessment(string jobSetId, JobFitAssessment jobFitAssessment)
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with { JobFitAssessment = jobFitAssessment });
    }

    public void SetActiveJobSetEvidenceSelection(EvidenceSelectionResult evidenceSelection)
    {
        SetJobSetEvidenceSelection(ActiveJobSetId, evidenceSelection);
    }

    public void SetJobSetEvidenceSelection(string jobSetId, EvidenceSelectionResult evidenceSelection)
    {
        UpdateJobSet(jobSetId, jobSet =>
        {
            var selectedIds = jobSet.SelectedEvidenceIds.Count > 0
                ? jobSet.SelectedEvidenceIds
                : evidenceSelection.SelectedEvidence.Select(static item => item.Evidence.Id).ToArray();

            var appliedSelection = new EvidenceSelectionResult(evidenceSelection.RankedEvidence
                .Select(item => item with { IsSelected = selectedIds.Contains(item.Evidence.Id, StringComparer.OrdinalIgnoreCase) })
                .ToArray());

            return jobSet with
            {
                EvidenceSelection = appliedSelection,
                SelectedEvidenceIds = appliedSelection.SelectedEvidence.Select(static item => item.Evidence.Id).ToArray()
            };
        });
    }

    public void SetActiveJobSetEvidenceSelected(string evidenceId, bool isSelected)
    {
        UpdateActiveJobSet(jobSet =>
        {
            var updated = BuildUpdatedEvidenceSelection(jobSet.EvidenceSelection, evidenceId, isSelected);
            return jobSet with
            {
                EvidenceSelection = updated,
                SelectedEvidenceIds = updated.SelectedEvidence.Select(static item => item.Evidence.Id).ToArray()
            };
        });
    }

    public void ClearActiveJobSetEvidenceSelections()
    {
        UpdateActiveJobSet(jobSet => jobSet with
        {
            SelectedEvidenceIds = Array.Empty<string>(),
            EvidenceSelection = new EvidenceSelectionResult(jobSet.EvidenceSelection.RankedEvidence
                .Select(item => item with { IsSelected = false })
                .ToArray())
        });
    }

    public int GetActiveSelectedEvidenceCount()
        => ActiveJobSet.SelectedEvidenceIds.Count > 0
            ? ActiveJobSet.SelectedEvidenceIds.Count
            : EvidenceSelection.SelectedEvidence.Count;

    private static EvidenceSelectionResult BuildUpdatedEvidenceSelection(EvidenceSelectionResult evidenceSelection, string evidenceId, bool isSelected)
        => new(evidenceSelection.RankedEvidence
            .Select(item => item.Evidence.Id.Equals(evidenceId, StringComparison.OrdinalIgnoreCase)
                    ? item with { IsSelected = isSelected }
                    : item)
                .ToArray());

    public void AddJobSet()
    {
        var nextSortOrder = GetNextSortOrder();
        var jobSet = CreateJobSet(nextSortOrder);

        jobSets.Add(jobSet);
        ActiveJobSetId = jobSet.Id;
        NotifyChanged();
    }

    public void DeleteJobSet(string jobSetId)
    {
        var index = jobSets.FindIndex(jobSet => jobSet.Id == jobSetId);
        if (index < 0)
        {
            throw new InvalidOperationException($"The job set '{jobSetId}' was not found.");
        }

        if (jobSets.Count == 1)
        {
            throw new InvalidOperationException("At least one job set must remain in the workspace.");
        }

        var wasActive = jobSets[index].Id == ActiveJobSetId;
        jobSets.RemoveAt(index);

        if (wasActive)
        {
            ActiveJobSetId = jobSets[Math.Min(index, jobSets.Count - 1)].Id;
        }

        NotifyChanged();
    }

    public void SelectJobSet(string jobSetId)
    {
        if (!jobSets.Any(jobSet => jobSet.Id == jobSetId))
        {
            throw new InvalidOperationException($"Job set '{jobSetId}' does not exist in this session.");
        }

        ActiveJobSetId = jobSetId;
        NotifyChanged();
    }

    public void UpdateActiveJobSetInputs(string jobUrl, string companyUrlsText)
    {
        UpdateActiveJobSet(jobSet => jobSet with
        {
            JobUrl = jobUrl,
            CompanyUrlsText = companyUrlsText
        });
    }

    public void SetImportResult(string exportPath, LinkedInExportImportResult importResult)
    {
        ClearAllGeneratedArtifacts(notifyChanged: false);
        ExportPath = exportPath;
        ImportResult = importResult;
        CandidateProfile = importResult.Profile;
        ClearJobFitAssessments(notifyChanged: false);
        ClearTechnologyGapAssessments(notifyChanged: false);
        ClearEvidenceSelections(notifyChanged: false);
        NotifyChanged();
    }

    public void SetApplicantDifferentiatorProfile(ApplicantDifferentiatorProfile differentiatorProfile)
    {
        ApplicantDifferentiatorProfile = differentiatorProfile;
        ClearJobFitAssessments(notifyChanged: false);
        ClearEvidenceSelections(notifyChanged: false, clearSelectedIds: false);
        NotifyChanged();
    }

    public void SetJobPosting(JobPostingAnalysis jobPosting)
    {
        UpdateActiveJobSet(jobSet => jobSet with
        {
            JobPosting = jobPosting,
            JobUrl = jobPosting.SourceUrl?.ToString() ?? jobSet.JobUrl,
            OutputFolderName = BuildOutputFolderName(jobSet.SortOrder, jobPosting),
            JobFitAssessment = JobFitAssessment.Empty,
            TechnologyGapAssessment = TechnologyGapAssessment.Empty,
            SelectedEvidenceIds = Array.Empty<string>(),
            EvidenceSelection = EvidenceSelectionResult.Empty,
            ProgressState = JobSetProgressState.NotStarted,
            ProgressDetail = "LLM work not started for this job set.",
            GeneratedDocuments = Array.Empty<GeneratedDocument>(),
            Exports = Array.Empty<DocumentExportResult>()
        });
    }

    public void SetCompanyProfile(CompanyResearchProfile companyProfile)
    {
        UpdateActiveJobSet(jobSet => jobSet with
        {
            CompanyProfile = companyProfile,
            JobFitAssessment = JobFitAssessment.Empty,
            TechnologyGapAssessment = TechnologyGapAssessment.Empty,
            EvidenceSelection = EvidenceSelectionResult.Empty,
            ProgressState = JobSetProgressState.NotStarted,
            ProgressDetail = "LLM work not started for this job set.",
            GeneratedDocuments = Array.Empty<GeneratedDocument>(),
            Exports = Array.Empty<DocumentExportResult>()
        });
    }

    public void SetLinkedInAuthorizationStatus(LinkedInAuthorizationStatus status)
    {
        LinkedInAuthorizationStatus = status;
        NotifyChanged();
    }

    public void SetOllamaAvailability(OllamaModelAvailability availability)
    {
        OllamaAvailability = availability;

        if (!availability.AvailableModels.Any(model => model.Equals(SelectedLlmModel, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedLlmModel = ResolvePreferredModel(availability.AvailableModels, ollamaOptions.Model);
            IsLlmSessionConfigured = false;
        }

        NotifyChanged();
    }

    public void SetLlmSessionSettings(string model, string thinkingLevel)
    {
        if (!CanEditLlmSessionSettings)
        {
            throw new InvalidOperationException("LLM session settings are locked after LLM-backed work starts.");
        }

        if (OllamaAvailability is null)
        {
            throw new InvalidOperationException("Check Ollama access before selecting the session model.");
        }

        var normalizedModel = model.Trim();
        if (string.IsNullOrWhiteSpace(normalizedModel))
        {
            throw new ArgumentException("A session model is required.", nameof(model));
        }

        if (!OllamaAvailability.AvailableModels.Any(value => value.Equals(normalizedModel, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"The selected model '{normalizedModel}' is not available from Ollama in this session.");
        }

        SelectedLlmModel = normalizedModel;
        SelectedThinkingLevel = NormalizeThinkingLevel(thinkingLevel);
        IsLlmSessionConfigured = true;
        NotifyChanged();
    }

    public void MarkLlmWorkStarted()
    {
        if (HasStartedLlmWork)
        {
            return;
        }

        HasStartedLlmWork = true;
        NotifyChanged();
    }

    public void SetGeneratedDocuments(IReadOnlyList<GeneratedDocument> documents, IReadOnlyList<DocumentExportResult> exports)
    {
        UpdateActiveJobSet(jobSet => jobSet with
        {
            ProgressState = JobSetProgressState.Done,
            ProgressDetail = "Markdown drafts generated for this job set.",
            GeneratedDocuments = documents,
            Exports = exports
        });
    }

    public void ClearGeneratedArtifacts(bool notifyChanged = true)
    {
        UpdateActiveJobSet(jobSet => jobSet with
        {
            ProgressState = JobSetProgressState.NotStarted,
            ProgressDetail = "LLM work not started for this job set.",
            GeneratedDocuments = Array.Empty<GeneratedDocument>(),
            Exports = Array.Empty<DocumentExportResult>()
        }, notifyChanged);
    }

    private void ClearAllGeneratedArtifacts(bool notifyChanged = true)
    {
        for (var index = 0; index < jobSets.Count; index++)
        {
            jobSets[index] = jobSets[index] with
            {
                ProgressState = JobSetProgressState.NotStarted,
                ProgressDetail = "LLM work not started for this job set.",
                GeneratedDocuments = Array.Empty<GeneratedDocument>(),
                Exports = Array.Empty<DocumentExportResult>()
            };
        }

        if (notifyChanged)
        {
            NotifyChanged();
        }
    }

    private void ClearTechnologyGapAssessments(bool notifyChanged = true)
    {
        for (var index = 0; index < jobSets.Count; index++)
        {
            jobSets[index] = jobSets[index] with { TechnologyGapAssessment = TechnologyGapAssessment.Empty };
        }

        if (notifyChanged)
        {
            NotifyChanged();
        }
    }

    private void ClearJobFitAssessments(bool notifyChanged = true)
    {
        for (var index = 0; index < jobSets.Count; index++)
        {
            jobSets[index] = jobSets[index] with { JobFitAssessment = JobFitAssessment.Empty };
        }

        if (notifyChanged)
        {
            NotifyChanged();
        }
    }

    private void ClearEvidenceSelections(bool notifyChanged = true, bool clearSelectedIds = true)
    {
        for (var index = 0; index < jobSets.Count; index++)
        {
            jobSets[index] = jobSets[index] with
            {
                EvidenceSelection = EvidenceSelectionResult.Empty,
                SelectedEvidenceIds = clearSelectedIds ? Array.Empty<string>() : jobSets[index].SelectedEvidenceIds
            };
        }

        if (notifyChanged)
        {
            NotifyChanged();
        }
    }

    private static string NormalizeThinkingLevel(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is not null && SupportedThinkingLevels.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : "medium";
    }

    private static string ResolvePreferredModel(IReadOnlyList<string> availableModels, string configuredModel)
    {
        var configuredMatch = availableModels.FirstOrDefault(model => model.Equals(configuredModel, StringComparison.OrdinalIgnoreCase));
        return configuredMatch ?? availableModels.FirstOrDefault() ?? string.Empty;
    }

    private int GetNextSortOrder()
        => jobSets.Count == 0 ? 1 : jobSets.Max(jobSet => jobSet.SortOrder) + 1;

    private static List<JobSetSessionState> LoadRecoveredJobSets(WorkspaceRecoveryStore? recoveryStore)
    {
        var snapshot = recoveryStore?.Load();
        if (snapshot?.JobSets is null || snapshot.JobSets.Count == 0)
        {
            return [CreateJobSet(1)];
        }

        return snapshot.JobSets
            .OrderBy(jobSet => jobSet.SortOrder)
            .Select(static jobSet => new JobSetSessionState
            {
                Id = jobSet.Id,
                SortOrder = jobSet.SortOrder,
                DefaultTitle = jobSet.DefaultTitle,
                OutputFolderName = jobSet.OutputFolderName,
                OutputLanguage = jobSet.OutputLanguage,
                ProgressState = jobSet.ProgressState == JobSetProgressState.Done && jobSet.Exports.Count > 0
                    ? JobSetProgressState.Done
                    : JobSetProgressState.NotStarted,
                ProgressDetail = jobSet.ProgressState == JobSetProgressState.Done && jobSet.Exports.Count > 0
                    ? jobSet.ProgressDetail
                    : "Recovered after restart. Re-import the profile before resuming this job set.",
                JobUrl = jobSet.JobUrl,
                CompanyUrlsText = jobSet.CompanyUrlsText,
                JobPosting = jobSet.JobPosting,
                CompanyProfile = jobSet.CompanyProfile,
                JobFitAssessment = JobFitAssessment.Empty,
                Exports = jobSet.Exports,
                GeneratedDocuments = Array.Empty<GeneratedDocument>(),
                TechnologyGapAssessment = TechnologyGapAssessment.Empty,
                SelectedEvidenceIds = jobSet.SelectedEvidenceIds ?? Array.Empty<string>(),
                EvidenceSelection = EvidenceSelectionResult.Empty
            })
            .ToList();
    }

    private static string LoadActiveJobSetId(WorkspaceRecoveryStore? recoveryStore)
    {
        var snapshot = recoveryStore?.Load();
        if (snapshot?.JobSets is null || snapshot.JobSets.Count == 0)
        {
            return CreateJobSetId(1);
        }

        return snapshot.JobSets.Any(jobSet => jobSet.Id == snapshot.ActiveJobSetId)
            ? snapshot.ActiveJobSetId
            : snapshot.JobSets.OrderBy(jobSet => jobSet.SortOrder).First().Id;
    }

    private static ApplicantDifferentiatorProfile LoadApplicantDifferentiatorProfile(WorkspaceRecoveryStore? recoveryStore)
        => recoveryStore?.Load()?.ApplicantDifferentiatorProfile ?? ApplicantDifferentiatorProfile.Empty;

    private void UpdateActiveJobSet(Func<JobSetSessionState, JobSetSessionState> update, bool notifyChanged = true)
        => UpdateJobSet(ActiveJobSetId, update, notifyChanged);

    private void UpdateJobSet(string jobSetId, Func<JobSetSessionState, JobSetSessionState> update, bool notifyChanged = true)
    {
        var index = jobSets.FindIndex(jobSet => jobSet.Id == jobSetId);
        if (index < 0)
        {
            throw new InvalidOperationException($"The job set '{jobSetId}' was not found.");
        }

        jobSets[index] = update(jobSets[index]);

        if (notifyChanged)
        {
            NotifyChanged();
        }
    }

    private static JobSetSessionState CreateJobSet(int sortOrder)
        => new()
        {
            Id = CreateJobSetId(sortOrder),
            SortOrder = sortOrder,
            DefaultTitle = $"Job set {sortOrder}",
            OutputFolderName = BuildOutputFolderName(sortOrder, null)
        };

    private static string CreateJobSetId(int sortOrder) => $"job-set-{sortOrder:00}";

    private static string BuildOutputFolderName(int sortOrder, JobPostingAnalysis? jobPosting)
    {
        var baseName = $"job-set-{sortOrder:00}";
        if (jobPosting is null)
        {
            return baseName;
        }

        var slug = Slugify($"{jobPosting.CompanyName}-{jobPosting.RoleTitle}");
        return string.IsNullOrWhiteSpace(slug)
            ? baseName
            : $"{baseName}-{slug}";
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder();
        var previousWasSeparator = false;

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSeparator = false;
                continue;
            }

            if (previousWasSeparator)
            {
                continue;
            }

            builder.Append('-');
            previousWasSeparator = true;
        }

        return builder.ToString().Trim('-');
    }

    private WorkspaceRecoverySnapshot CreateRecoverySnapshot()
        => new(
            ActiveJobSetId,
            jobSets.Select(static jobSet => new JobSetRecoveryState(
                jobSet.Id,
                jobSet.SortOrder,
                jobSet.DefaultTitle,
                jobSet.OutputFolderName,
                jobSet.OutputLanguage,
                jobSet.ProgressState,
                jobSet.ProgressDetail,
                jobSet.JobUrl,
                jobSet.CompanyUrlsText,
                jobSet.JobPosting,
                jobSet.CompanyProfile,
                jobSet.Exports,
                jobSet.SelectedEvidenceIds)).ToArray(),
            ApplicantDifferentiatorProfile);

    private void NotifyChanged()
    {
        recoveryStateStore?.Save(CreateRecoverySnapshot());
        Changed?.Invoke();
    }
}
