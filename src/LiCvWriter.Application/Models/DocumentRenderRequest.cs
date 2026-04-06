using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Application.Models;

public sealed record DocumentRenderRequest(
    DocumentKind Kind,
    CandidateProfile Candidate,
    JobPostingAnalysis JobPosting,
    string? CompanyContext,
    string? GeneratedBody = null,
    OutputLanguage OutputLanguage = OutputLanguage.English,
    JobFitAssessment? JobFitAssessment = null,
    ApplicantDifferentiatorProfile? ApplicantDifferentiatorProfile = null,
    EvidenceSelectionResult? EvidenceSelection = null);
