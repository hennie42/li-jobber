using LiCvWriter.Application.Models;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Web.Services;

public sealed record WorkspaceRecoverySnapshot(
    string ActiveJobSetId,
    IReadOnlyList<JobSetRecoveryState> JobSets,
    ApplicantDifferentiatorProfile? ApplicantDifferentiatorProfile = null);

public sealed record JobSetRecoveryState(
    string Id,
    int SortOrder,
    string DefaultTitle,
    string OutputFolderName,
    OutputLanguage OutputLanguage,
    JobSetProgressState ProgressState,
    string ProgressDetail,
    string JobUrl,
    string CompanyUrlsText,
    JobPostingAnalysis? JobPosting,
    CompanyResearchProfile? CompanyProfile,
    IReadOnlyList<DocumentExportResult> Exports,
    IReadOnlyList<string>? SelectedEvidenceIds = null);