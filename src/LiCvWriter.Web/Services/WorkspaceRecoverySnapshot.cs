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
    DraftGenerationPreferences? DraftGenerationPreferences = null,
    LinkedInImportDiagnosticsSnapshot? LinkedInImportDiagnostics = null,
    LinkedInAuthorizationStatus? LinkedInAuthorizationStatus = null,
    IReadOnlyDictionary<string, OllamaCapacityVerdict>? CapacityVerdicts = null,
    ModelBenchmarkSession? LastBenchmarkSession = null,
    IReadOnlyList<string>? HiddenSuggestionUrls = null,
    IReadOnlyList<SavedSuggestionListState>? SavedSuggestionLists = null);

public sealed record DraftGenerationPreferences
{
    public bool GenerateCv { get; init; } = true;

    public bool GenerateCoverLetter { get; init; } = true;

    public bool GenerateSummary { get; init; } = true;

    public bool GenerateRecommendations { get; init; } = true;

    public bool GenerateInterviewNotes { get; init; } = true;

    public string ContactEmail { get; init; } = string.Empty;

    public string ContactPhone { get; init; } = string.Empty;

    public string ContactLinkedIn { get; init; } = string.Empty;

    public string ContactCity { get; init; } = string.Empty;
}

public sealed record SavedSuggestionListState(
    string ProviderId,
    string ProviderDisplayName,
    string Query,
    string PreferredLocation,
    IReadOnlyList<JobDiscoverySuggestionReview> Suggestions);

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
    bool IsSelectedForBatch = false,
    string? LastFitReviewFingerprint = null,
    bool LastFitReviewIncludedLlmEnhancement = false,
    JobSetSourceLanguage InputLanguage = JobSetSourceLanguage.Auto,
    DateOnly? ManualApplicationDeadlineOverride = null);
