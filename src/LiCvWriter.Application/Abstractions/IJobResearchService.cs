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
        CancellationToken cancellationToken = default);

    Task<CompanyResearchProfile> BuildCompanyProfileAsync(
        IEnumerable<Uri> sourceUrls,
        string? selectedModel = null,
        string? selectedThinkingLevel = null,
        Action<LlmProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);
}
