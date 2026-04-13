using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Infrastructure.Workflows;

public sealed class InsightsDiscoveryApplicantDifferentiatorDraftingService(ILlmClient llmClient, OllamaOptions ollamaOptions)
{
    private const int MaxPromptCharacters = 24_000;

    public async Task<ApplicantDifferentiatorProfile> DraftAsync(
        string extractedText,
        string selectedModel,
        string selectedThinkingLevel,
        Action<LlmProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedSource = NormalizeSourceText(extractedText);
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            throw new InvalidOperationException("The uploaded Insights Discovery PDF did not produce enough readable text to draft applicant differentiators.");
        }

        var response = await llmClient.GenerateAsync(
            new LlmRequest(
                string.IsNullOrWhiteSpace(selectedModel) ? ollamaOptions.Model : selectedModel,
                BuildSystemPrompt(),
                [new LlmChatMessage("user", BuildUserPrompt(normalizedSource))],
                UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                Stream: true,
                Think: string.IsNullOrWhiteSpace(selectedThinkingLevel) ? ollamaOptions.Think : selectedThinkingLevel,
                KeepAlive: ollamaOptions.KeepAlive,
                Temperature: 0.1),
            progress is null ? null : update => progress(update with
            {
                Message = "Drafting applicant differentiators",
                Detail = string.IsNullOrWhiteSpace(update.Detail)
                    ? $"Applicant differentiator drafting is running via {update.Model}."
                    : update.Detail
            }),
            cancellationToken);

        if (!TryParse(response.Content, out var differentiatorProfile))
        {
            throw new InvalidOperationException("The model did not return a valid applicant differentiator draft.");
        }

        if (!differentiatorProfile.HasContent)
        {
            throw new InvalidOperationException("The model returned an empty applicant differentiator draft.");
        }

        return differentiatorProfile;
    }

    private static string BuildSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("You turn an Insights Discovery profile into reusable applicant differentiator notes for a job search assistant.");
        builder.AppendLine();
        builder.AppendLine("Return JSON only with this exact shape:");
        builder.AppendLine(
            """
            {
              "workStyle": "",
              "communicationStyle": "",
              "leadershipStyle": "",
              "stakeholderStyle": "",
              "motivators": "",
              "targetNarrative": "",
              "watchouts": "",
              "aboutApplicantBasis": ""
            }
            """);
        builder.AppendLine();
        builder.AppendLine("Field expectations:");

        foreach (var field in ApplicantDifferentiatorFieldCatalog.All)
        {
            builder.Append("- ")
                .Append(field.Key)
                .Append(": ")
                .Append(field.Label)
                .Append(". ")
                .Append(field.Meaning)
                .Append(" Used for: ")
                .Append(field.Usage)
                .AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("Rules:");
        builder.AppendLine($"- {PromptConstraints.JsonOnlyOutput}");
        builder.AppendLine("- Use concise, reusable job-search language.");
        builder.AppendLine("- Prefer 1-3 short sentences per field.");
        builder.AppendLine("- Generalize the source instead of copying raw assessment text verbatim.");
        builder.AppendLine("- Avoid names, employer-specific confidential details, or color shorthand unless the source makes it essential.");
        builder.AppendLine("- If the source does not support a field, return an empty string for that field.");
        return builder.ToString();
    }

    private static string BuildUserPrompt(string normalizedSource)
        => $"""
Draft applicant differentiators from the Insights Discovery profile text below.

The output will be saved into an application form that later steers fit review, evidence selection, and generated drafts.

Insights Discovery profile text:
{normalizedSource}
""";

    private static string NormalizeSourceText(string extractedText)
    {
        if (string.IsNullOrWhiteSpace(extractedText))
        {
            return string.Empty;
        }

        var normalized = extractedText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n").Trim();

        if (normalized.Length <= MaxPromptCharacters)
        {
            return normalized;
        }

        return normalized[..MaxPromptCharacters].TrimEnd()
            + Environment.NewLine
            + Environment.NewLine
            + "[Source truncated for prompt length.]";
    }

    private static bool TryParse(string content, out ApplicantDifferentiatorProfile differentiatorProfile)
    {
        try
        {
            var json = ExtractJsonObject(content);
            var draft = JsonSerializer.Deserialize<ApplicantDifferentiatorDraft>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (draft is null)
            {
                differentiatorProfile = ApplicantDifferentiatorProfile.Empty;
                return false;
            }

            differentiatorProfile = new ApplicantDifferentiatorProfile
            {
                WorkStyle = NullIfWhiteSpace(draft.WorkStyle),
                CommunicationStyle = NullIfWhiteSpace(draft.CommunicationStyle),
                LeadershipStyle = NullIfWhiteSpace(draft.LeadershipStyle),
                StakeholderStyle = NullIfWhiteSpace(draft.StakeholderStyle),
                Motivators = NullIfWhiteSpace(draft.Motivators),
                TargetNarrative = NullIfWhiteSpace(draft.TargetNarrative),
                Watchouts = NullIfWhiteSpace(draft.Watchouts),
                AboutApplicantBasis = NullIfWhiteSpace(draft.AboutApplicantBasis)
            };
            return true;
        }
        catch
        {
            differentiatorProfile = ApplicantDifferentiatorProfile.Empty;
            return false;
        }
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

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class ApplicantDifferentiatorDraft
    {
        public string? WorkStyle { get; init; }

        public string? CommunicationStyle { get; init; }

        public string? LeadershipStyle { get; init; }

        public string? StakeholderStyle { get; init; }

        public string? Motivators { get; init; }

        public string? TargetNarrative { get; init; }

        public string? Watchouts { get; init; }

        public string? AboutApplicantBasis { get; init; }
    }
}
