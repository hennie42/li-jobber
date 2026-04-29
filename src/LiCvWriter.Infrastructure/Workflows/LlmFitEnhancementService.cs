using System.Text;
using System.Text.Json;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Infrastructure.Workflows;

/// <summary>
/// Sends Partial and Missing fit-review requirements to the LLM for semantic evidence
/// matching, then merges upgrades back into the baseline assessment.  The deterministic
/// review remains the floor — the LLM can only upgrade matches, never downgrade.
/// </summary>
public sealed class LlmFitEnhancementService(
    ILlmClient llmClient,
    OllamaOptions ollamaOptions)
{
    private readonly LlmJsonInvoker jsonInvoker = new(llmClient);

    /// <summary>
    /// Enhances a deterministic <see cref="JobFitAssessment"/> by asking the LLM to
    /// re-evaluate Partial and Missing requirements against the full candidate profile.
    /// Returns the original assessment unchanged when all requirements are already Strong
    /// or if the LLM call fails.
    /// </summary>
    public async Task<JobFitAssessment> EnhanceAsync(
        JobFitAssessment baseline,
        CandidateProfile candidateProfile,
        JobPostingAnalysis jobPosting,
        CompanyResearchProfile? companyProfile,
        string selectedModel,
        string selectedThinkingLevel,
        Action<LlmProgressUpdate>? progress = null,
        string? sourceLanguageHint = null,
        CancellationToken cancellationToken = default)
    {
        var candidateRequirements = baseline.Requirements
            .Where(static requirement => requirement.Match != JobRequirementMatch.Strong)
            .ToArray();

        if (candidateRequirements.Length == 0)
        {
            return baseline;
        }

        try
        {
            var result = await jsonInvoker.InvokeAsync(
                new LlmRequest(
                    string.IsNullOrWhiteSpace(selectedModel) ? ollamaOptions.Model : selectedModel,
                    BuildSystemPrompt(sourceLanguageHint),
                    [new LlmChatMessage("user", BuildUserPrompt(candidateRequirements, candidateProfile, jobPosting, companyProfile))],
                    UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                    Stream: true,
                    Think: string.IsNullOrWhiteSpace(selectedThinkingLevel) ? ollamaOptions.Think : selectedThinkingLevel,
                    KeepAlive: ollamaOptions.KeepAlive,
                    Temperature: 0.0,
                    ResponseFormat: LlmResponseFormat.Json),
                ParseEnhancement,
                progress is null ? null : update => progress(update with
                {
                    Message = "Enhancing fit review with LLM",
                    Detail = string.IsNullOrWhiteSpace(update.Detail)
                        ? $"Semantic evidence matching is running via {update.Model}."
                        : update.Detail
                }),
                cancellationToken);

            return result.Value is { } parsed ? Merge(baseline, parsed) : baseline;
        }
        catch
        {
            return baseline;
        }
    }

    internal static JobFitAssessment Merge(JobFitAssessment baseline, EnhancementParseResult parseResult)
    {
        var enhancements = parseResult.Enhancements;

        var lookup = enhancements.ToDictionary(
            static enhancement => enhancement.Requirement,
            StringComparer.OrdinalIgnoreCase);

        var anyUpgraded = false;
        var merged = baseline.Requirements.Select(requirement =>
        {
            if (!lookup.TryGetValue(requirement.Requirement, out var enhancement))
            {
                return requirement;
            }

            var proposedMatch = ParseMatch(enhancement.NewMatch);
            if (proposedMatch is null || !IsUpgrade(requirement.Match, proposedMatch.Value))
            {
                return requirement;
            }

            anyUpgraded = true;
            return requirement with
            {
                Match = proposedMatch.Value,
                SupportingEvidence = enhancement.Evidence.Count > 0 ? enhancement.Evidence : requirement.SupportingEvidence,
                Rationale = string.IsNullOrWhiteSpace(enhancement.Rationale) ? requirement.Rationale : enhancement.Rationale,
                IsLlmEnhanced = true
            };
        }).ToArray();

        var hasStrategicEnhancements = parseResult.GapFramingStrategies.Count > 0
            || !string.IsNullOrWhiteSpace(parseResult.PositioningAngle);

        if (!anyUpgraded && !hasStrategicEnhancements)
        {
            return baseline;
        }

        var result = anyUpgraded
            ? JobFitScoring.BuildAssessment(merged, isLlmEnhanced: true)
            : baseline with { IsLlmEnhanced = true };

        return result with
        {
            GapFramingStrategies = parseResult.GapFramingStrategies.Count > 0
                ? parseResult.GapFramingStrategies
                : result.GapFramingStrategies,
            PositioningAngle = !string.IsNullOrWhiteSpace(parseResult.PositioningAngle)
                ? parseResult.PositioningAngle
                : result.PositioningAngle
        };
    }

    private static bool IsUpgrade(JobRequirementMatch current, JobRequirementMatch proposed)
        => (current, proposed) switch
        {
            (JobRequirementMatch.Missing, JobRequirementMatch.Partial) => true,
            (JobRequirementMatch.Missing, JobRequirementMatch.Strong) => true,
            (JobRequirementMatch.Partial, JobRequirementMatch.Strong) => true,
            _ => false
        };

    private static JobRequirementMatch? ParseMatch(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "strong" => JobRequirementMatch.Strong,
            "partial" => JobRequirementMatch.Partial,
            _ => null
        };

    private static string BuildSystemPrompt(string? sourceLanguageHint = null)
    {
        var languageLine = string.IsNullOrWhiteSpace(sourceLanguageHint)
            ? string.Empty
            : $"Job and company text are written in {sourceLanguageHint}; the candidate profile may be in English. Match evidence across languages.\n\n";
        return languageLine + $$"""
            You are a semantic evidence matcher for candidate fit reviews.

            Given a list of job requirements that were NOT strongly matched by keyword search,
            and a full candidate profile (including recommendations, experience descriptions,
            and project descriptions), identify requirements where the candidate has supporting
            evidence that keyword matching missed.

            Focus especially on:
            - Recommendations: third-party endorsements often describe capabilities in different words than job postings use.
            - Experience descriptions: long-form text may contain evidence that doesn't share exact keywords.
            - Project descriptions: hands-on work that demonstrates capability.

            Return JSON only with this exact shape:
            {
              "enhancedRequirements": [
                {
                  "requirement": "The exact requirement text",
                  "newMatch": "Strong or Partial",
                  "evidence": ["Evidence title or description"],
                  "rationale": "Brief explanation of why this evidence supports the requirement"
                }
              ],
              "gapFramingStrategies": [
                "For each remaining gap or partial requirement, a short reframing strategy that positions the candidate positively. Examples: 'No Kubernetes, but deep Docker + cloud infra experience', 'Adopted Terraform within 3 months at previous role'."
              ],
              "positioningAngle": "A single paragraph (2-3 sentences) describing the candidate's most compelling competitive angle for this specific role — what makes them uniquely valuable vs. typical applicants."
            }

            Rules:
            - {{PromptConstraints.JsonOnlyOutput}}
            - {{PromptConstraints.SourceTextBoundary}}
            - Only include requirements in enhancedRequirements where you found genuine supporting evidence.
            - Set newMatch to "Strong" only when the evidence clearly and directly supports the requirement.
            - Set newMatch to "Partial" when the evidence is indirect but relevant.
            - Be conservative: only upgrade when the semantic connection is clear.
            - For gapFramingStrategies, write reframing strategies only for requirements that remain Partial or Missing after your enhancements. Frame gaps as transferable strengths or learning velocity — never mention the gap itself.
            - For positioningAngle, identify the candidate's unique combination of skills, experience depth, and domain knowledge that a typical applicant for this role would lack.
            """;
    }

    private static string BuildUserPrompt(
        IReadOnlyList<JobRequirementAssessment> candidateRequirements,
        CandidateProfile candidateProfile,
        JobPostingAnalysis jobPosting,
        CompanyResearchProfile? companyProfile)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"Role: {jobPosting.RoleTitle} at {jobPosting.CompanyName}");
        builder.AppendLine($"Summary: {jobPosting.Summary}");

        if (companyProfile is not null && !string.IsNullOrWhiteSpace(companyProfile.Summary))
        {
            builder.AppendLine($"Company: {companyProfile.Summary}");
        }

        builder.AppendLine();
        builder.AppendLine("Requirements to re-evaluate (not matched by keyword search):");

        foreach (var requirement in candidateRequirements)
        {
            builder.AppendLine($"- [{requirement.Importance}] {requirement.Requirement} (current: {requirement.Match})");
        }

        builder.AppendLine();
        builder.AppendLine("Candidate profile:");

        if (!string.IsNullOrWhiteSpace(candidateProfile.Headline))
        {
            builder.AppendLine($"Headline: {candidateProfile.Headline}");
        }

        if (!string.IsNullOrWhiteSpace(candidateProfile.Summary))
        {
            builder.AppendLine($"Summary: {candidateProfile.Summary}");
        }

        if (candidateProfile.Experience.Count > 0)
        {
            builder.AppendLine();

            foreach (var role in candidateProfile.Experience)
            {
                var desc = string.IsNullOrWhiteSpace(role.Description) ? string.Empty : $" — {role.Description}";
                builder.AppendLine($"- {role.Title} @ {role.CompanyName} ({role.Period.DisplayValue}){desc}");
            }
        }

        if (candidateProfile.Recommendations.Count > 0)
        {
            builder.AppendLine();

            foreach (var rec in candidateProfile.Recommendations)
            {
                builder.AppendLine($"- Rec from {rec.Author.FullName} ({rec.JobTitle} at {rec.Company}): {rec.Text}");
            }
        }

        if (candidateProfile.Projects.Count > 0)
        {
            builder.AppendLine();

            foreach (var project in candidateProfile.Projects)
            {
                builder.AppendLine($"- Project {project.Title}: {project.Description}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Find requirements with genuine semantic evidence that keyword matching missed.");

        return builder.ToString();
    }

    private static EnhancementParseResult? ParseEnhancement(string content)
    {
        var json = LlmJsonInvoker.ExtractJsonObject(content);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var enhancements = new List<EnhancedRequirement>();
        if (root.TryGetProperty("enhancedRequirements", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                var requirement = ReadString(element, "requirement");
                var newMatch = ReadString(element, "newMatch");
                var rationale = ReadString(element, "rationale");
                var evidence = ReadStringArray(element, "evidence");

                if (!string.IsNullOrWhiteSpace(requirement) && !string.IsNullOrWhiteSpace(newMatch))
                {
                    enhancements.Add(new EnhancedRequirement(requirement, newMatch, evidence, rationale ?? string.Empty));
                }
            }
        }

        var gapFramingStrategies = ReadStringArray(root, "gapFramingStrategies");
        var positioningAngle = ReadString(root, "positioningAngle");

        if (enhancements.Count == 0 && gapFramingStrategies.Count == 0 && string.IsNullOrWhiteSpace(positioningAngle))
        {
            return null;
        }

        return new EnhancementParseResult(enhancements, gapFramingStrategies, positioningAngle);
    }

    internal static bool TryParse(string content, out EnhancementParseResult result)
    {
        try
        {
            var parsed = ParseEnhancement(content);
            if (parsed is null)
            {
                result = EnhancementParseResult.Empty;
                return false;
            }

            result = parsed;
            return true;
        }
        catch
        {
            result = EnhancementParseResult.Empty;
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString()?.Trim();
        }

        return null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray()!;
    }

    internal sealed record EnhancedRequirement(
        string Requirement,
        string NewMatch,
        IReadOnlyList<string> Evidence,
        string Rationale);

    internal sealed record EnhancementParseResult(
        IReadOnlyList<EnhancedRequirement> Enhancements,
        IReadOnlyList<string> GapFramingStrategies,
        string? PositioningAngle)
    {
        public static EnhancementParseResult Empty { get; } = new(
            Array.Empty<EnhancedRequirement>(),
            Array.Empty<string>(),
            null);
    }
}
