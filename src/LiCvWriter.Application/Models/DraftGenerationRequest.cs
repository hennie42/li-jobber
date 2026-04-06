using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Application.Models;

public sealed record DraftGenerationRequest(
    CandidateProfile Candidate,
    JobPostingAnalysis JobPosting,
    string? CompanyContext,
    string? AdditionalInstructions,
    IReadOnlyList<DocumentKind> DocumentKinds,
    bool ExportToFiles,
    string? LlmModel = null,
    string? LlmThinkingLevel = null,
    string? ExportFolder = null,
    OutputLanguage OutputLanguage = OutputLanguage.English,
    JobFitAssessment? JobFitAssessment = null,
    ApplicantDifferentiatorProfile? ApplicantDifferentiatorProfile = null,
    EvidenceSelectionResult? EvidenceSelection = null);
