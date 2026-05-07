using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Application.Services;

public sealed class JobDiscoverySuggestionService(
    IJobDiscoveryService discoveryService,
    IJobResearchService jobResearchService,
    JobFitAnalysisService jobFitAnalysisService,
    EvidenceSelectionService evidenceSelectionService)
{
    public async Task<IReadOnlyList<JobDiscoverySuggestionReview>> DiscoverAsync(
        JobDiscoverySearchPlan searchPlan,
        CandidateProfile? candidateProfile,
        ApplicantDifferentiatorProfile? differentiatorProfile = null,
        string? selectedModel = null,
        string? selectedThinkingLevel = null,
        Action<JobDiscoveryProgressUpdate>? progress = null,
        bool enrichWithFit = true,
        CancellationToken cancellationToken = default)
    {
        var suggestions = await discoveryService.DiscoverAsync(searchPlan, progress, cancellationToken);
        if (suggestions.Count == 0)
        {
            return Array.Empty<JobDiscoverySuggestionReview>();
        }

        if (!enrichWithFit || candidateProfile is null)
        {
            return suggestions
                .Select(static suggestion => JobDiscoverySuggestionReview.FromRaw(suggestion))
                .ToArray();
        }

        var reviewTasks = suggestions
            .Select((suggestion, index) => ReviewSuggestionAsync(
                suggestion,
                index,
                suggestions.Count,
                candidateProfile,
                differentiatorProfile,
                selectedModel,
                selectedThinkingLevel,
                progress,
                cancellationToken))
            .ToArray();

        var reviews = await Task.WhenAll(reviewTasks);

        return reviews
            .OrderByDescending(static review => review.HasFitAssessment)
            .ThenByDescending(static review => GetRecommendationRank(review.JobFitAssessment.Recommendation))
            .ThenByDescending(static review => review.JobFitAssessment.OverallScore)
            .ThenBy(static review => review.Suggestion.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<JobDiscoverySuggestionReview> ReviewSuggestionAsync(
        JobDiscoverySuggestion suggestion,
        int index,
        int suggestionCount,
        CandidateProfile candidateProfile,
        ApplicantDifferentiatorProfile? differentiatorProfile,
        string? selectedModel,
        string? selectedThinkingLevel,
        Action<JobDiscoveryProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Invoke(new JobDiscoveryProgressUpdate(
            $"Analyzing suggestion {index + 1} of {suggestionCount}",
            suggestion.Title));

        try
        {
            var jobPosting = await jobResearchService.AnalyzeAsync(
                suggestion.DetailUrl,
                selectedModel,
                selectedThinkingLevel,
                cancellationToken: cancellationToken);
            var fitAssessment = jobFitAnalysisService.Analyze(candidateProfile, jobPosting, companyProfile: null, differentiatorProfile);
            var evidenceSelection = fitAssessment.HasSignals
                ? evidenceSelectionService.Build(candidateProfile, jobPosting, companyProfile: null, fitAssessment, differentiatorProfile)
                : EvidenceSelectionResult.Empty;

            return new JobDiscoverySuggestionReview(
                suggestion,
                jobPosting,
                fitAssessment,
                evidenceSelection);
        }
        catch (Exception exception)
        {
            return JobDiscoverySuggestionReview.FromRaw(suggestion, exception.Message);
        }
    }

    private static int GetRecommendationRank(JobFitRecommendation recommendation)
        => recommendation switch
        {
            JobFitRecommendation.Apply => 3,
            JobFitRecommendation.Stretch => 2,
            JobFitRecommendation.Skip => 1,
            _ => 0
        };
}