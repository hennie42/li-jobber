using LiCvWriter.Application.Models;
using LiCvWriter.Core.Jobs;

namespace LiCvWriter.Application.Abstractions;

public interface IJobResearchService
{
    Task<JobPostingAnalysis> AnalyzeAsync(
        Uri jobPostingUrl,
        string? selectedModel = null,
        string? selectedThinkingLevel = null,
        Action<LlmProgressUpdate>? progress = null,
        string? sourceLanguageHint = null,
        CancellationToken cancellationToken = default);

    Task<CompanyProfileBuildResult> BuildCompanyProfileAsync(
        IEnumerable<Uri> sourceUrls,
        string? selectedModel = null,
        string? selectedThinkingLevel = null,
        Action<LlmProgressUpdate>? progress = null,
        string? sourceLanguageHint = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Uri>> DiscoverCompanyContextUrlsAsync(
        Uri jobPostingUrl,
        string? companyName = null,
        CancellationToken cancellationToken = default);

    Task<JobPostingAnalysis> AnalyzeTextAsync(
        string jobPostingText,
        string? selectedModel = null,
        string? selectedThinkingLevel = null,
        Action<LlmProgressUpdate>? progress = null,
        string? sourceLanguageHint = null,
        CancellationToken cancellationToken = default);

    Task<CompanyResearchProfile> BuildCompanyProfileFromTextAsync(
        string companyContextText,
        string? selectedModel = null,
        string? selectedThinkingLevel = null,
        Action<LlmProgressUpdate>? progress = null,
        string? sourceLanguageHint = null,
        CancellationToken cancellationToken = default);
}
