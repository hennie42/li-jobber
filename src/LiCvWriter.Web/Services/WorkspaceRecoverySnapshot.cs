using LiCvWriter.Application.Models;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Infrastructure.LinkedIn;

namespace LiCvWriter.Web.Services;

public sealed record WorkspaceRecoverySnapshot(
    IReadOnlyList<JobSetRecoveryState> JobSets,
    ApplicantDifferentiatorProfile? ApplicantDifferentiatorProfile = null,
    CandidateProfile? CandidateProfile = null,
    string SelectedLlmModel = "",
    string SelectedThinkingLevel = "",
    LinkedInImportDiagnosticsSnapshot? LinkedInImportDiagnostics = null,
    LinkedInAuthorizationStatus? LinkedInAuthorizationStatus = null);

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
    IReadOnlyList<string>? SelectedEvidenceIds = null,
    JobSetInputMode InputMode = JobSetInputMode.LinkToUrls,
    string JobPostingText = "",
    string CompanyContextText = "",
    JobFitAssessment? JobFitAssessment = null,
    TechnologyGapAssessment? TechnologyGapAssessment = null,
    EvidenceSelectionResult? EvidenceSelection = null,
    IReadOnlyList<GeneratedDocument>? GeneratedDocuments = null,
    string AdditionalInstructions = "",
    string? LastFitReviewFingerprint = null,
    bool LastFitReviewIncludedLlmEnhancement = false,
    JobSetSourceLanguage InputLanguage = JobSetSourceLanguage.Auto);