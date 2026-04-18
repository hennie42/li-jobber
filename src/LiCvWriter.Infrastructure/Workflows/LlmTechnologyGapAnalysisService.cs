using System.Text;
using System.Text.Json;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Infrastructure.Workflows;

public sealed class LlmTechnologyGapAnalysisService(ILlmClient llmClient, OllamaOptions ollamaOptions)
{
    public async Task<TechnologyGapAssessment> AnalyzeAsync(
        CandidateProfile candidateProfile,
        JobPostingAnalysis jobPosting,
        CompanyResearchProfile? companyProfile,
        string selectedModel,
        string selectedThinkingLevel,
        Action<LlmProgressUpdate>? progress = null,
        string? sourceLanguageHint = null,
        CancellationToken cancellationToken = default)
    {
        var response = await llmClient.GenerateAsync(
            new LlmRequest(
                string.IsNullOrWhiteSpace(selectedModel) ? ollamaOptions.Model : selectedModel,
                BuildSystemPrompt(sourceLanguageHint),
                [new LlmChatMessage("user", BuildUserPrompt(candidateProfile, jobPosting, companyProfile))],
                UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                Stream: true,
                Think: string.IsNullOrWhiteSpace(selectedThinkingLevel) ? ollamaOptions.Think : selectedThinkingLevel,
                KeepAlive: ollamaOptions.KeepAlive,
                Temperature: 0.1),
            progress is null ? null : update => progress(update with
            {
                Message = "Analyzing technology gaps",
                Detail = string.IsNullOrWhiteSpace(update.Detail)
                    ? $"Technology gap analysis is running via {update.Model}."
                    : update.Detail
            }),
            cancellationToken);

        return TryParse(response.Content, out var assessment)
            ? assessment
            : TechnologyGapAnalyzer.Analyze(candidateProfile, jobPosting, companyProfile);
    }

    private static string BuildSystemPrompt(string? sourceLanguageHint = null)
    {
        var languageLine = string.IsNullOrWhiteSpace(sourceLanguageHint)
            ? string.Empty
            : $"Job and company text are written in {sourceLanguageHint}; the candidate profile may be in English. Match technology terms across languages.\n\n";
        return languageLine + $$"""
You analyze technology alignment between a candidate profile and a target job.

Return JSON only with this exact shape:
{
  "detectedTechnologies": ["Technology"],
  "possiblyUnderrepresentedTechnologies": ["Technology"]
}

Rules:
- {{PromptConstraints.JsonOnlyOutput}}
- Use concise technology labels.
- `detectedTechnologies`: the most relevant modern technologies explicitly or strongly implied by the job and company context.
- `possiblyUnderrepresentedTechnologies`: only technologies from `detectedTechnologies` that seem weak or absent in the candidate evidence.
- Prefer grounded source-backed requirement signals and aliases when provided.
""";
    }

    private static string BuildUserPrompt(CandidateProfile candidateProfile, JobPostingAnalysis jobPosting, CompanyResearchProfile? companyProfile)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Role: {jobPosting.RoleTitle} at {jobPosting.CompanyName}");
        builder.AppendLine($"Summary: {jobPosting.Summary}");

        var sourceSignals = jobPosting.Signals
            .Concat(companyProfile?.Signals ?? Array.Empty<JobContextSignal>())
            .Where(static signal => !string.IsNullOrWhiteSpace(signal.Requirement))
            .Take(12)
            .ToArray();

        if (sourceSignals.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Signals:");

            foreach (var signal in sourceSignals)
            {
                builder.Append($"- {signal.Category}: {signal.Requirement}");

                if (signal.EffectiveAliases.Count > 0)
                {
                    builder.Append($" (aliases: {string.Join(", ", signal.EffectiveAliases)})");
                }

                if (!string.IsNullOrWhiteSpace(signal.SourceSnippet))
                {
                    builder.Append($" | {signal.SourceSnippet}");
                }

                builder.AppendLine();
            }
        }

        if (companyProfile is not null)
        {
            builder.AppendLine($"Company: {companyProfile.Summary}");
        }

        builder.AppendLine();
        builder.AppendLine($"Candidate: {candidateProfile.Headline} | {candidateProfile.Summary}");
        builder.AppendLine($"Certifications: {string.Join(", ", candidateProfile.Certifications.Select(static c => c.Name))}");
        builder.AppendLine($"Projects: {string.Join(" | ", candidateProfile.Projects.Select(static p => $"{p.Title}: {p.Description}"))}");
        builder.AppendLine($"Experience: {string.Join(" | ", candidateProfile.Experience.Take(8).Select(static r => $"{r.Title}: {r.Description}"))}");

        if (candidateProfile.ManualSignals.Count > 0)
        {
            builder.AppendLine($"Notes: {string.Join(" | ", candidateProfile.ManualSignals.Values)}");
        }

        builder.AppendLine();
        builder.AppendLine("Identify relevant newer technologies from the job context and mark which are possibly underrepresented on the profile.");
        return builder.ToString();
    }

    private static bool TryParse(string content, out TechnologyGapAssessment assessment)
    {
        try
        {
            var json = ExtractJsonObject(content);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var detected = ReadArray(root, "detectedTechnologies");
            var missing = ReadArray(root, "possiblyUnderrepresentedTechnologies")
                .Where(item => detected.Contains(item, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            assessment = new TechnologyGapAssessment(detected, missing);
            return true;
        }
        catch
        {
            assessment = TechnologyGapAssessment.Empty;
            return false;
        }
    }

    private static string[] ReadArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    private static string ExtractJsonObject(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var lines = trimmed.Split('\n');
            trimmed = string.Join('\n', lines.Skip(1).Take(lines.Length - 2));
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }

        return trimmed;
    }
}
