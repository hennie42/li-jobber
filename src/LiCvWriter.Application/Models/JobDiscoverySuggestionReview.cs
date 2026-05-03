using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Application.Models;

public sealed record JobDiscoverySuggestionReview(
    JobDiscoverySuggestion Suggestion,
    JobPostingAnalysis? JobPosting,
    JobFitAssessment JobFitAssessment,
    EvidenceSelectionResult EvidenceSelection,
    string AnalysisError = "")
{
    public bool HasJobPosting => JobPosting is not null;

    public bool HasFitAssessment => JobFitAssessment.HasSignals;

    public bool HasEvidenceSelection => EvidenceSelection.HasSignals;

    public bool HasAnalysisError => !string.IsNullOrWhiteSpace(AnalysisError);

    public IReadOnlyList<string> WhySuggested
        => JobFitAssessment.Strengths.Count > 0
            ? JobFitAssessment.Strengths.Take(2).ToArray()
            : Array.Empty<string>();

    public static JobDiscoverySuggestionReview FromRaw(JobDiscoverySuggestion suggestion, string? analysisError = null)
        => new(suggestion, null, JobFitAssessment.Empty, EvidenceSelectionResult.Empty, analysisError ?? string.Empty);
}