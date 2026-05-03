using LiCvWriter.Application.Models;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;


namespace LiCvWriter.Web.Services;

public enum JobSetSubtask
{
    JobContext,
    FitReview,
    TechnologyGap,
    DraftGeneration
}

public enum JobSetSubtaskStatus
{
    NotStarted,
    Running,
    Done
}

public sealed record JobSetSessionState
{
    public required string Id { get; init; }

    public required int SortOrder { get; init; }

    public required string DefaultTitle { get; init; }

    public required string OutputFolderName { get; init; }

    public JobSetInputMode InputMode { get; init; } = JobSetInputMode.LinkToUrls;

    public OutputLanguage OutputLanguage { get; init; } = OutputLanguage.English;

    public JobSetSourceLanguage InputLanguage { get; init; } = JobSetSourceLanguage.Auto;

    public JobSetProgressState ProgressState { get; init; } = JobSetProgressState.NotStarted;

    public string ProgressDetail { get; init; } = "LLM work not started for this job set.";

    public JobSetSubtask? ActiveSubtask { get; init; }

    public string JobUrl { get; init; } = string.Empty;

    public string CompanyUrlsText { get; init; } = string.Empty;

    public string JobPostingText { get; init; } = string.Empty;

    public string CompanyContextText { get; init; } = string.Empty;

    public string AdditionalInstructions { get; init; } = string.Empty;

    public bool IsSelectedForBatch { get; init; }

    /// <summary>
    /// Manual application deadline override that wins over freshly extracted deadlines
    /// until the user clears it.
    /// </summary>
    public DateOnly? ManualApplicationDeadlineOverride { get; init; }

    public JobPostingAnalysis? JobPosting { get; init; }

    public CompanyResearchProfile? CompanyProfile { get; init; }

    public JobFitAssessment JobFitAssessment { get; init; } = JobFitAssessment.Empty;

    public TechnologyGapAssessment TechnologyGapAssessment { get; init; } = TechnologyGapAssessment.Empty;

    public IReadOnlyList<string> SelectedEvidenceIds { get; init; } = Array.Empty<string>();

    public EvidenceSelectionResult EvidenceSelection { get; init; } = EvidenceSelectionResult.Empty;

    public IReadOnlyList<GeneratedDocument> GeneratedDocuments { get; init; } = Array.Empty<GeneratedDocument>();

    public IReadOnlyList<DocumentExportResult> Exports { get; init; } = Array.Empty<DocumentExportResult>();

    public string? LastFitReviewFingerprint { get; init; }

    public bool LastFitReviewIncludedLlmEnhancement { get; init; }

    public string Title => JobPosting is not null
        ? $"{JobPosting.RoleTitle} @ {JobPosting.CompanyName}".Trim()
        : DefaultTitle;

    public JobSetSubtaskStatus GetSubtaskStatus(JobSetSubtask subtask) => subtask switch
    {
        JobSetSubtask.JobContext => ActiveSubtask == JobSetSubtask.JobContext
            ? JobSetSubtaskStatus.Running
            : JobPosting is not null ? JobSetSubtaskStatus.Done : JobSetSubtaskStatus.NotStarted,
        JobSetSubtask.FitReview => ActiveSubtask == JobSetSubtask.FitReview
            ? JobSetSubtaskStatus.Running
            : JobFitAssessment.HasSignals ? JobSetSubtaskStatus.Done : JobSetSubtaskStatus.NotStarted,
        JobSetSubtask.TechnologyGap => ActiveSubtask == JobSetSubtask.TechnologyGap
            ? JobSetSubtaskStatus.Running
            : TechnologyGapAssessment.HasSignals ? JobSetSubtaskStatus.Done : JobSetSubtaskStatus.NotStarted,
        JobSetSubtask.DraftGeneration => ActiveSubtask == JobSetSubtask.DraftGeneration
            ? JobSetSubtaskStatus.Running
            : GeneratedDocuments.Count > 0 ? JobSetSubtaskStatus.Done : JobSetSubtaskStatus.NotStarted,
        _ => JobSetSubtaskStatus.NotStarted
    };
}