using System.Text;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Infrastructure.LinkedIn;

namespace LiCvWriter.Web.Services;

public sealed class WorkspaceSession(OllamaOptions ollamaOptions, WorkspaceRecoveryStore? recoveryStore = null)
{
    private static readonly string[] SupportedThinkingLevels = ["low", "medium", "high"];
    private readonly object gate = new();
    private readonly WorkspaceRecoveryStore? recoveryStateStore = recoveryStore;
    private readonly List<JobSetSessionState> jobSets = LoadRecoveredJobSets(recoveryStore);
    private string exportPath = Path.Combine(Environment.CurrentDirectory, "LI-export");
    private CandidateProfile? candidateProfile = LoadCandidateProfile(recoveryStore);
    private LinkedInExportImportResult? importResult;
    private LinkedInImportDiagnosticsSnapshot? linkedInImportDiagnostics = LoadLinkedInImportDiagnostics(recoveryStore);
    private ApplicantDifferentiatorProfile applicantDifferentiatorProfile = LoadApplicantDifferentiatorProfile(recoveryStore);
    private LinkedInAuthorizationStatus linkedInAuthorizationStatus = LoadLinkedInAuthorizationStatus(recoveryStore);
    private OllamaModelAvailability? ollamaAvailability;
    private string selectedLlmModel = LoadSelectedLlmModel(recoveryStore, ollamaOptions.Model);
    private string selectedThinkingLevel = LoadSelectedThinkingLevel(recoveryStore, ollamaOptions.Think);
    private bool isLlmSessionConfigured;
    private bool hasStartedLlmWork;

    public event Action? Changed;

    public string ExportPath => Read(() => exportPath);

    public CandidateProfile? CandidateProfile => Read(() => candidateProfile);

    public LinkedInExportImportResult? ImportResult => Read(() => importResult);

    public LinkedInImportDiagnosticsSnapshot? LinkedInImportDiagnostics => Read(() => linkedInImportDiagnostics);

    public ApplicantDifferentiatorProfile ApplicantDifferentiatorProfile => Read(() => applicantDifferentiatorProfile);

    public IReadOnlyList<JobSetSessionState> JobSets => Read(() => jobSets.ToArray());

    public JobSetSessionState GetJobSet(string jobSetId) => Read(() => GetJobSetUnsafe(jobSetId));

    public int GetSelectedEvidenceCount(string jobSetId) => Read(() =>
    {
        var jobSet = GetJobSetUnsafe(jobSetId);
        return jobSet.SelectedEvidenceIds.Count > 0
            ? jobSet.SelectedEvidenceIds.Count
            : jobSet.EvidenceSelection.SelectedEvidence.Count;
    });

    public bool CanGenerateForJobSet(string jobSetId) => Read(() =>
        candidateProfile is not null && GetJobSetUnsafe(jobSetId).JobPosting is not null);

    public bool CanStartDraftGenerationForJobSet(string jobSetId) => Read(() =>
        candidateProfile is not null
        && GetJobSetUnsafe(jobSetId).JobPosting is not null
        && ollamaAvailability is not null
        && isLlmSessionConfigured
        && ollamaAvailability.AvailableModels.Any(model => model.Equals(selectedLlmModel, StringComparison.OrdinalIgnoreCase)));

    public LinkedInAuthorizationStatus LinkedInAuthorizationStatus => Read(() => linkedInAuthorizationStatus);

    public OllamaModelAvailability? OllamaAvailability => Read(() => ollamaAvailability);

    public string SelectedLlmModel => Read(() => selectedLlmModel);

    public string SelectedThinkingLevel => Read(() => selectedThinkingLevel);

    public bool IsLlmSessionConfigured => Read(() => isLlmSessionConfigured);

    public bool HasStartedLlmWork => Read(() => hasStartedLlmWork);

    public bool IsLlmReady => Read(() => ollamaAvailability is not null
        && isLlmSessionConfigured
        && ollamaAvailability.AvailableModels.Any(model => model.Equals(selectedLlmModel, StringComparison.OrdinalIgnoreCase)));

    public bool CanEditLlmSessionSettings => true;

    public bool IsJobSetFitReviewCurrent(string jobSetId, string fingerprint, bool requiresLlmEnhancement)
    {
        return Read(() =>
        {
            var jobSet = jobSets.FirstOrDefault(job => job.Id.Equals(jobSetId, StringComparison.OrdinalIgnoreCase));
            if (jobSet is null)
            {
                throw new InvalidOperationException($"The job set '{jobSetId}' was not found.");
            }

            if (!jobSet.JobFitAssessment.HasSignals || !jobSet.EvidenceSelection.HasSignals)
            {
                return false;
            }

            if (!string.Equals(jobSet.LastFitReviewFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            return !requiresLlmEnhancement
                || jobSet.LastFitReviewIncludedLlmEnhancement
                || jobSet.JobFitAssessment.Requirements.All(static requirement => requirement.Match == JobRequirementMatch.Strong);
        });
    }

    public void RecordJobSetFitReviewRefresh(string jobSetId, string fingerprint, bool includedLlmEnhancement)
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with
        {
            LastFitReviewFingerprint = fingerprint,
            LastFitReviewIncludedLlmEnhancement = includedLlmEnhancement
        });
    }

    public void SetJobSetInputLanguage(string jobSetId, JobSetSourceLanguage inputLanguage)
    {
        UpdateJobSet(jobSetId, jobSet =>
        {
            if (jobSet.InputLanguage == inputLanguage)
            {
                return jobSet;
            }

            // Changing the source language invalidates prior parses, fit review,
            // evidence ranking, technology gap and generated drafts because the
            // next refresh will run with a different language hint.
            return jobSet with
            {
                InputLanguage = inputLanguage,
                JobPosting = null,
                CompanyProfile = null,
                JobFitAssessment = JobFitAssessment.Empty,
                TechnologyGapAssessment = TechnologyGapAssessment.Empty,
                SelectedEvidenceIds = Array.Empty<string>(),
                EvidenceSelection = EvidenceSelectionResult.Empty,
                LastFitReviewFingerprint = null,
                LastFitReviewIncludedLlmEnhancement = false,
                ProgressState = JobSetProgressState.NotStarted,
                ProgressDetail = "Source language changed; re-run analysis to refresh this job set.",
                GeneratedDocuments = Array.Empty<GeneratedDocument>(),
                Exports = Array.Empty<DocumentExportResult>()
            };
        });
    }

    public void MarkJobSetRunning(string jobSetId, string detail)
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with
        {
            ProgressState = JobSetProgressState.Running,
            ProgressDetail = detail
        });
    }

    public void MarkJobSetFailed(string jobSetId, string detail)
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with
        {
            ProgressState = JobSetProgressState.Failed,
            ProgressDetail = detail
        });
    }

    public void ResetJobSetProgress(string jobSetId, string detail = "LLM work not started for this job set.")
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with
        {
            ProgressState = JobSetProgressState.NotStarted,
            ProgressDetail = detail
        });
    }

    public void SetJobSetTechnologyGapAssessment(string jobSetId, TechnologyGapAssessment technologyGapAssessment)
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with { TechnologyGapAssessment = technologyGapAssessment });
    }

    public void SetJobSetJobFitAssessment(string jobSetId, JobFitAssessment jobFitAssessment)
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with { JobFitAssessment = jobFitAssessment });
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

    public void SetJobSetOutputLanguage(string jobSetId, OutputLanguage outputLanguage)
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with { OutputLanguage = outputLanguage });
    }

    public void SetJobSetEvidenceSelected(string jobSetId, string evidenceId, bool isSelected)
    {
        UpdateJobSet(jobSetId, jobSet =>
        {
            var updated = BuildUpdatedEvidenceSelection(jobSet.EvidenceSelection, evidenceId, isSelected);
            return jobSet with
            {
                EvidenceSelection = updated,
                SelectedEvidenceIds = updated.SelectedEvidence.Select(static item => item.Evidence.Id).ToArray()
            };
        });
    }

    public void ClearJobSetEvidenceSelections(string jobSetId)
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with
        {
            SelectedEvidenceIds = Array.Empty<string>(),
            EvidenceSelection = new EvidenceSelectionResult(jobSet.EvidenceSelection.RankedEvidence
                .Select(item => item with { IsSelected = false })
                .ToArray())
        });
    }

    public void SelectAllJobSetEvidence(string jobSetId)
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with
        {
            SelectedEvidenceIds = jobSet.EvidenceSelection.RankedEvidence
                .Select(item => item.Evidence.Id)
                .ToArray(),
            EvidenceSelection = new EvidenceSelectionResult(jobSet.EvidenceSelection.RankedEvidence
                .Select(item => item with { IsSelected = true })
                .ToArray())
        });
    }

    public void UpdateJobSetInputs(string jobSetId, string jobUrl, string companyUrlsText, string jobPostingText, string companyContextText)
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with
        {
            JobUrl = jobUrl,
            CompanyUrlsText = companyUrlsText,
            JobPostingText = jobPostingText,
            CompanyContextText = companyContextText
        });
    }

    public void SetJobSetAdditionalInstructions(string jobSetId, string? additionalInstructions)
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with { AdditionalInstructions = additionalInstructions ?? string.Empty });
    }

    private static EvidenceSelectionResult BuildUpdatedEvidenceSelection(EvidenceSelectionResult evidenceSelection, string evidenceId, bool isSelected)
        => new(evidenceSelection.RankedEvidence
            .Select(item => item.Evidence.Id.Equals(evidenceId, StringComparison.OrdinalIgnoreCase)
                    ? item with { IsSelected = isSelected }
                    : item)
                .ToArray());

    public void AddJobSet(JobSetInputMode inputMode = JobSetInputMode.LinkToUrls)
    {
        lock (gate)
        {
            var nextSortOrder = GetNextSortOrderUnsafe();
            var jobSet = CreateJobSet(nextSortOrder, inputMode);

            jobSets.Add(jobSet);
        }

        NotifyChanged();
    }

    public void DeleteJobSet(string jobSetId)
    {
        lock (gate)
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

            jobSets.RemoveAt(index);
        }

        NotifyChanged();
    }

    public void SetImportResult(string exportPath, LinkedInExportImportResult importResult)
    {
        lock (gate)
        {
            ClearAllGeneratedArtifactsUnsafe();
            this.exportPath = exportPath;
            this.importResult = importResult;
            linkedInImportDiagnostics = LinkedInImportDiagnosticsFormatter.BuildSnapshot(importResult);
            candidateProfile = importResult.Profile;
            ClearJobFitAssessmentsUnsafe();
            ClearTechnologyGapAssessmentsUnsafe();
            ClearEvidenceSelectionsUnsafe();
        }

        NotifyChanged();
    }

    public void UpdateCandidateProfile(CandidateProfile updatedProfile)
    {
        lock (gate)
        {
            candidateProfile = updatedProfile;

            if (importResult is not null)
            {
                importResult = importResult with { Profile = updatedProfile };
                linkedInImportDiagnostics = LinkedInImportDiagnosticsFormatter.BuildSnapshot(importResult);
            }

            ClearAllGeneratedArtifactsUnsafe();
            ClearJobFitAssessmentsUnsafe();
            ClearTechnologyGapAssessmentsUnsafe();
            ClearEvidenceSelectionsUnsafe();
        }

        NotifyChanged();
    }

    public void SetApplicantDifferentiatorProfile(ApplicantDifferentiatorProfile differentiatorProfile)
    {
        lock (gate)
        {
            applicantDifferentiatorProfile = differentiatorProfile;
            ClearJobFitAssessmentsUnsafe();
            ClearEvidenceSelectionsUnsafe(clearSelectedIds: false);
        }

        NotifyChanged();
    }

    public void SetJobSetJobPosting(string jobSetId, JobPostingAnalysis jobPosting)
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with
        {
            JobPosting = jobPosting,
            JobUrl = jobPosting.SourceUrl?.ToString() ?? jobSet.JobUrl,
            OutputFolderName = BuildOutputFolderName(jobSet.SortOrder, jobPosting),
            JobFitAssessment = JobFitAssessment.Empty,
            TechnologyGapAssessment = TechnologyGapAssessment.Empty,
            SelectedEvidenceIds = Array.Empty<string>(),
            EvidenceSelection = EvidenceSelectionResult.Empty,
            LastFitReviewFingerprint = null,
            LastFitReviewIncludedLlmEnhancement = false,
            ProgressState = JobSetProgressState.NotStarted,
            ProgressDetail = "LLM work not started for this job set.",
            GeneratedDocuments = Array.Empty<GeneratedDocument>(),
            Exports = Array.Empty<DocumentExportResult>()
        });
    }

    public void SetJobSetCompanyProfile(string jobSetId, CompanyResearchProfile companyProfile)
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with
        {
            CompanyProfile = companyProfile,
            JobFitAssessment = JobFitAssessment.Empty,
            TechnologyGapAssessment = TechnologyGapAssessment.Empty,
            EvidenceSelection = EvidenceSelectionResult.Empty,
            LastFitReviewFingerprint = null,
            LastFitReviewIncludedLlmEnhancement = false,
            ProgressState = JobSetProgressState.NotStarted,
            ProgressDetail = "LLM work not started for this job set.",
            GeneratedDocuments = Array.Empty<GeneratedDocument>(),
            Exports = Array.Empty<DocumentExportResult>()
        });
    }

    public void SetLinkedInAuthorizationStatus(LinkedInAuthorizationStatus status)
    {
        lock (gate)
        {
            linkedInAuthorizationStatus = status;
        }

        NotifyChanged();
    }

    public void SetOllamaAvailability(OllamaModelAvailability availability)
    {
        lock (gate)
        {
            ollamaAvailability = availability;

            if (!availability.AvailableModels.Any(model => model.Equals(selectedLlmModel, StringComparison.OrdinalIgnoreCase)))
            {
                selectedLlmModel = ResolvePreferredModel(availability.AvailableModels, ollamaOptions.Model);
                isLlmSessionConfigured = false;
            }
            else if (!isLlmSessionConfigured)
            {
                isLlmSessionConfigured = true;
            }
        }

        NotifyChanged();
    }

    public void SetLlmSessionSettings(string model, string thinkingLevel)
    {
        lock (gate)
        {
            if (ollamaAvailability is null)
            {
                throw new InvalidOperationException("Check Ollama access before selecting the session model.");
            }

            var normalizedModel = model.Trim();
            if (string.IsNullOrWhiteSpace(normalizedModel))
            {
                throw new ArgumentException("A session model is required.", nameof(model));
            }

            if (!ollamaAvailability.AvailableModels.Any(value => value.Equals(normalizedModel, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"The selected model '{normalizedModel}' is not available from Ollama in this session.");
            }

            selectedLlmModel = normalizedModel;
            selectedThinkingLevel = NormalizeThinkingLevel(thinkingLevel);
            isLlmSessionConfigured = true;
        }

        NotifyChanged();
    }

    public void MarkLlmWorkStarted()
    {
        var shouldNotify = false;
        lock (gate)
        {
            if (!hasStartedLlmWork)
            {
                hasStartedLlmWork = true;
                shouldNotify = true;
            }
        }

        if (shouldNotify)
        {
            NotifyChanged();
        }
    }

    public void SetJobSetGeneratedDocuments(string jobSetId, IReadOnlyList<GeneratedDocument> documents, IReadOnlyList<DocumentExportResult> exports)
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with
        {
            ProgressState = JobSetProgressState.Done,
            ProgressDetail = "Markdown drafts generated for this job set.",
            GeneratedDocuments = documents,
            Exports = exports
        });
    }

    public void ClearJobSetGeneratedArtifacts(string jobSetId, bool notifyChanged = true)
    {
        UpdateJobSet(jobSetId, jobSet => jobSet with
        {
            ProgressState = JobSetProgressState.NotStarted,
            ProgressDetail = "LLM work not started for this job set.",
            GeneratedDocuments = Array.Empty<GeneratedDocument>(),
            Exports = Array.Empty<DocumentExportResult>()
        }, notifyChanged);
    }

    private void ClearAllGeneratedArtifacts(bool notifyChanged = true)
    {
        lock (gate)
        {
            ClearAllGeneratedArtifactsUnsafe();
        }

        if (notifyChanged)
        {
            NotifyChanged();
        }
    }

    private void ClearAllGeneratedArtifactsUnsafe()
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
    }

    private void ClearTechnologyGapAssessments(bool notifyChanged = true)
    {
        lock (gate)
        {
            ClearTechnologyGapAssessmentsUnsafe();
        }

        if (notifyChanged)
        {
            NotifyChanged();
        }
    }

    private void ClearTechnologyGapAssessmentsUnsafe()
    {
        for (var index = 0; index < jobSets.Count; index++)
        {
            jobSets[index] = jobSets[index] with { TechnologyGapAssessment = TechnologyGapAssessment.Empty };
        }
    }

    private void ClearJobFitAssessments(bool notifyChanged = true)
    {
        lock (gate)
        {
            ClearJobFitAssessmentsUnsafe();
        }

        if (notifyChanged)
        {
            NotifyChanged();
        }
    }

    private void ClearJobFitAssessmentsUnsafe()
    {
        for (var index = 0; index < jobSets.Count; index++)
        {
            jobSets[index] = jobSets[index] with
            {
                JobFitAssessment = JobFitAssessment.Empty,
                LastFitReviewFingerprint = null,
                LastFitReviewIncludedLlmEnhancement = false
            };
        }
    }

    private void ClearEvidenceSelections(bool notifyChanged = true, bool clearSelectedIds = true)
    {
        lock (gate)
        {
            ClearEvidenceSelectionsUnsafe(clearSelectedIds);
        }

        if (notifyChanged)
        {
            NotifyChanged();
        }
    }

    private void ClearEvidenceSelectionsUnsafe(bool clearSelectedIds = true)
    {
        for (var index = 0; index < jobSets.Count; index++)
        {
            jobSets[index] = jobSets[index] with
            {
                EvidenceSelection = EvidenceSelectionResult.Empty,
                LastFitReviewFingerprint = null,
                LastFitReviewIncludedLlmEnhancement = false,
                SelectedEvidenceIds = clearSelectedIds ? Array.Empty<string>() : jobSets[index].SelectedEvidenceIds
            };
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

    private int GetNextSortOrderUnsafe()
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
            .Select(static jobSet =>
            {
                var isInterrupted = jobSet.ProgressState == JobSetProgressState.Running;

                return new JobSetSessionState
                {
                    Id = jobSet.Id,
                    SortOrder = jobSet.SortOrder,
                    DefaultTitle = jobSet.DefaultTitle,
                    OutputFolderName = jobSet.OutputFolderName,
                    InputMode = jobSet.InputMode,
                    OutputLanguage = jobSet.OutputLanguage,
                    InputLanguage = jobSet.InputLanguage,
                    ProgressState = isInterrupted ? JobSetProgressState.NotStarted : jobSet.ProgressState,
                    ProgressDetail = isInterrupted ? "Recovered after restart." : jobSet.ProgressDetail,
                    JobUrl = jobSet.JobUrl,
                    CompanyUrlsText = jobSet.CompanyUrlsText,
                    JobPostingText = jobSet.JobPostingText,
                    CompanyContextText = jobSet.CompanyContextText,
                    AdditionalInstructions = jobSet.AdditionalInstructions,
                    JobPosting = jobSet.JobPosting,
                    CompanyProfile = jobSet.CompanyProfile,
                    JobFitAssessment = jobSet.JobFitAssessment ?? JobFitAssessment.Empty,
                    Exports = jobSet.Exports,
                    GeneratedDocuments = jobSet.GeneratedDocuments ?? Array.Empty<GeneratedDocument>(),
                    TechnologyGapAssessment = jobSet.TechnologyGapAssessment ?? TechnologyGapAssessment.Empty,
                    SelectedEvidenceIds = jobSet.SelectedEvidenceIds ?? Array.Empty<string>(),
                    EvidenceSelection = jobSet.EvidenceSelection ?? EvidenceSelectionResult.Empty,
                    LastFitReviewFingerprint = jobSet.LastFitReviewFingerprint,
                    LastFitReviewIncludedLlmEnhancement = jobSet.LastFitReviewIncludedLlmEnhancement
                };
            })
            .ToList();
    }

    private static ApplicantDifferentiatorProfile LoadApplicantDifferentiatorProfile(WorkspaceRecoveryStore? recoveryStore)
        => recoveryStore?.Load()?.ApplicantDifferentiatorProfile ?? ApplicantDifferentiatorProfile.Empty;

    private static CandidateProfile? LoadCandidateProfile(WorkspaceRecoveryStore? recoveryStore)
        => recoveryStore?.Load()?.CandidateProfile;

    private static LinkedInImportDiagnosticsSnapshot? LoadLinkedInImportDiagnostics(WorkspaceRecoveryStore? recoveryStore)
    {
        var snapshot = recoveryStore?.Load();
        if (snapshot is null)
        {
            return null;
        }

        // Use the persisted diagnostics snapshot if present (modern recovery format).
        if (snapshot.LinkedInImportDiagnostics is not null)
        {
            return snapshot.LinkedInImportDiagnostics;
        }

        // Backwards compatibility: recover the diagnostics view from the CandidateProfile when
        // the recovery file predates the diagnostics snapshot field.
        if (snapshot.CandidateProfile is { } profile)
        {
            return LinkedInImportDiagnosticsFormatter.BuildProfileOnlySnapshot(profile);
        }

        return null;
    }

    private static LinkedInAuthorizationStatus LoadLinkedInAuthorizationStatus(WorkspaceRecoveryStore? recoveryStore)
        => recoveryStore?.Load()?.LinkedInAuthorizationStatus
            ?? new LinkedInAuthorizationStatus(false, "DMA member snapshot not loaded.", null, null, null);

    private static string LoadSelectedLlmModel(WorkspaceRecoveryStore? recoveryStore, string configuredModel)
    {
        var recoveredModel = recoveryStore?.Load()?.SelectedLlmModel;
        return string.IsNullOrWhiteSpace(recoveredModel) ? configuredModel : recoveredModel;
    }

    private static string LoadSelectedThinkingLevel(WorkspaceRecoveryStore? recoveryStore, string configuredThinkingLevel)
    {
        var recoveredThinkingLevel = recoveryStore?.Load()?.SelectedThinkingLevel;
        return NormalizeThinkingLevel(string.IsNullOrWhiteSpace(recoveredThinkingLevel) ? configuredThinkingLevel : recoveredThinkingLevel);
    }

    private T Read<T>(Func<T> read)
    {
        lock (gate)
        {
            return read();
        }
    }

    private JobSetSessionState GetJobSetUnsafe(string jobSetId)
    {
        var jobSet = jobSets.FirstOrDefault(item => item.Id == jobSetId);
        if (jobSet is null)
        {
            throw new InvalidOperationException($"The job set '{jobSetId}' was not found.");
        }
        return jobSet;
    }

    private void UpdateJobSet(string jobSetId, Func<JobSetSessionState, JobSetSessionState> update, bool notifyChanged = true)
    {
        lock (gate)
        {
            var index = jobSets.FindIndex(jobSet => jobSet.Id == jobSetId);
            if (index < 0)
            {
                throw new InvalidOperationException($"The job set '{jobSetId}' was not found.");
            }

            jobSets[index] = update(jobSets[index]);
        }

        if (notifyChanged)
        {
            NotifyChanged();
        }
    }

    private static JobSetSessionState CreateJobSet(int sortOrder, JobSetInputMode inputMode = JobSetInputMode.LinkToUrls)
        => new()
        {
            Id = CreateJobSetId(sortOrder),
            SortOrder = sortOrder,
            DefaultTitle = $"Job set {sortOrder}",
            OutputFolderName = BuildOutputFolderName(sortOrder, null),
            InputMode = inputMode
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
        => Read(CreateRecoverySnapshotUnsafe);

    private WorkspaceRecoverySnapshot CreateRecoverySnapshotUnsafe()
        => new(
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
                jobSet.SelectedEvidenceIds,
                jobSet.InputMode,
                jobSet.JobPostingText,
                jobSet.CompanyContextText,
                jobSet.JobFitAssessment,
                jobSet.TechnologyGapAssessment,
                jobSet.EvidenceSelection,
                jobSet.GeneratedDocuments,
                jobSet.AdditionalInstructions,
                jobSet.LastFitReviewFingerprint,
                jobSet.LastFitReviewIncludedLlmEnhancement,
                jobSet.InputLanguage)).ToArray(),
            applicantDifferentiatorProfile,
            candidateProfile,
            selectedLlmModel,
            selectedThinkingLevel,
            linkedInImportDiagnostics,
            linkedInAuthorizationStatus);

    private void NotifyChanged()
    {
        recoveryStateStore?.Save(CreateRecoverySnapshot());
        Changed?.Invoke();
    }
}
