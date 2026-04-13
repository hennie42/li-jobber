using LiCvWriter.Application.Models;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;


namespace LiCvWriter.Web.Services;

public sealed record JobSetSessionState
{
    public required string Id { get; init; }

    public required int SortOrder { get; init; }

    public required string DefaultTitle { get; init; }

    public required string OutputFolderName { get; init; }

    public JobSetInputMode InputMode { get; init; } = JobSetInputMode.LinkToUrls;

    public OutputLanguage OutputLanguage { get; init; } = OutputLanguage.English;

    public JobSetProgressState ProgressState { get; init; } = JobSetProgressState.NotStarted;

    public string ProgressDetail { get; init; } = "LLM work not started for this job set.";

    public string JobUrl { get; init; } = string.Empty;

    public string CompanyUrlsText { get; init; } = string.Empty;

    public string JobPostingText { get; init; } = string.Empty;

    public string CompanyContextText { get; init; } = string.Empty;

    public JobPostingAnalysis? JobPosting { get; init; }

    public CompanyResearchProfile? CompanyProfile { get; init; }

    public JobFitAssessment JobFitAssessment { get; init; } = JobFitAssessment.Empty;

    public TechnologyGapAssessment TechnologyGapAssessment { get; init; } = TechnologyGapAssessment.Empty;

    public IReadOnlyList<string> SelectedEvidenceIds { get; init; } = Array.Empty<string>();

    public EvidenceSelectionResult EvidenceSelection { get; init; } = EvidenceSelectionResult.Empty;

    public IReadOnlyList<GeneratedDocument> GeneratedDocuments { get; init; } = Array.Empty<GeneratedDocument>();

    public IReadOnlyList<DocumentExportResult> Exports { get; init; } = Array.Empty<DocumentExportResult>();

    public string Title => JobPosting is not null
        ? $"{JobPosting.RoleTitle} @ {JobPosting.CompanyName}".Trim()
        : DefaultTitle;
}