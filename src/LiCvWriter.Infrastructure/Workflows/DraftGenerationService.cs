using System.Diagnostics;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Auditing;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Infrastructure.Workflows;

public sealed class DraftGenerationService(
    ILlmClient llmClient,
    IDocumentRenderer documentRenderer,
    IDocumentExportService documentExportService,
    IAuditStore auditStore,
    OllamaOptions ollamaOptions) : IDraftGenerationService
{
    public async Task<DraftGenerationResult> GenerateAsync(
        DraftGenerationRequest request,
        Action<LlmProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var documents = new List<GeneratedDocument>();
        var exports = new List<DocumentExportResult>();
        var selectedModel = string.IsNullOrWhiteSpace(request.LlmModel) ? ollamaOptions.Model : request.LlmModel.Trim();
        var selectedThinkingLevel = string.IsNullOrWhiteSpace(request.LlmThinkingLevel) ? ollamaOptions.Think : request.LlmThinkingLevel.Trim();
        var outputLanguage = request.OutputLanguage;

        foreach (var kind in request.DocumentKinds.Distinct())
        {
            var documentStopwatch = Stopwatch.StartNew();
            var systemPrompt = GetSystemPrompt(kind, outputLanguage, request.JobPosting);
            var userPrompt = BuildUserPrompt(kind, request);

            var response = await llmClient.GenerateAsync(new LlmRequest(
                selectedModel,
                systemPrompt,
                [new LlmChatMessage("user", userPrompt)],
                UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                Stream: true,
                Think: selectedThinkingLevel,
                KeepAlive: ollamaOptions.KeepAlive,
                Temperature: ollamaOptions.Temperature),
                progress is null ? null : update => progress(PrefixProgress(kind, update)),
                cancellationToken);

            var renderedDocument = await documentRenderer.RenderAsync(new DocumentRenderRequest(
                kind,
                request.Candidate,
                request.JobPosting,
                request.CompanyContext,
                response.Content,
                outputLanguage,
                request.JobFitAssessment,
                request.ApplicantDifferentiatorProfile,
                request.EvidenceSelection), cancellationToken);

            var document = (string.IsNullOrWhiteSpace(request.ExportFolder)
                ? renderedDocument
                : renderedDocument with { OutputPath = request.ExportFolder.Trim() }) with
            {
                LlmDuration = response.Duration,
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens,
                Model = response.Model
            };

            documents.Add(document);
            progress?.Invoke(new LlmProgressUpdate(
                $"Generated {kind}",
                $"{kind} completed in {FormatDuration(response.Duration)}.",
                response.Model,
                response.Duration,
                Completed: true,
                PromptTokens: response.PromptTokens,
                CompletionTokens: response.CompletionTokens,
                EstimatedRemaining: TimeSpan.Zero));

            DocumentExportResult? export = null;
            if (request.ExportToFiles)
            {
                export = await documentExportService.ExportAsync(document, cancellationToken);
                exports.Add(export);
            }

            await auditStore.SaveAsync(new AuditTrailEntry(
                DateTimeOffset.UtcNow,
                $"Generate-{kind}",
                $"Generated {kind} content for {request.JobPosting.RoleTitle} at {request.JobPosting.CompanyName}.",
                new Dictionary<string, string>
                {
                    ["Model"] = response.Model,
                    ["ThinkingLevel"] = selectedThinkingLevel,
                    ["DocumentKind"] = kind.ToString(),
                    ["Company"] = request.JobPosting.CompanyName,
                    ["RoleTitle"] = request.JobPosting.RoleTitle,
                    ["ExportFolder"] = document.OutputPath ?? string.Empty,
                    ["OutputLanguage"] = outputLanguage.ToString(),
                    ["FitRecommendation"] = request.JobFitAssessment?.Recommendation.ToString() ?? string.Empty,
                    ["SelectedEvidenceCount"] = request.EvidenceSelection?.SelectedEvidence.Count.ToString() ?? "0",
                    ["PromptTokens"] = response.PromptTokens?.ToString() ?? string.Empty,
                    ["CompletionTokens"] = response.CompletionTokens?.ToString() ?? string.Empty,
                    ["Duration"] = response.Duration?.ToString() ?? string.Empty,
                    ["MarkdownPath"] = export?.MarkdownPath ?? string.Empty
                }), cancellationToken);

            documentStopwatch.Stop();
            progress?.Invoke(new LlmProgressUpdate(
                $"Completed {kind}",
                $"{kind} finished in {FormatDuration(documentStopwatch.Elapsed)} (LLM: {FormatDuration(response.Duration)}).",
                response.Model,
                documentStopwatch.Elapsed,
                Completed: true,
                PromptTokens: response.PromptTokens,
                CompletionTokens: response.CompletionTokens,
                EstimatedRemaining: TimeSpan.Zero));
        }

        return new DraftGenerationResult(documents, exports);
    }

    private static LlmProgressUpdate PrefixProgress(DocumentKind kind, LlmProgressUpdate update)
        => update with
        {
            Message = $"Generating {kind}",
            Detail = string.IsNullOrWhiteSpace(update.Detail)
                ? $"{kind} is streaming from {update.Model}."
                : $"{kind}: {update.Detail}"
        };

    private static string FormatDuration(TimeSpan? duration)
    {
        if (duration is null)
        {
            return "unknown time";
        }

        var value = duration.Value;
        return value.TotalMinutes >= 1
            ? value.ToString(@"m\:ss")
            : value.ToString(@"s\.f\s");
    }

    private static string GetSystemPrompt(DocumentKind kind, OutputLanguage outputLanguage, JobPostingAnalysis jobPosting)
    {
        var role = jobPosting.RoleTitle;
        var company = jobPosting.CompanyName;
        var danishNameRule = " Keep technology names, company names, quoted job phrases, and file names in their original or English form.";

        return (outputLanguage, kind) switch
        {
            (OutputLanguage.Danish, DocumentKind.Cv) =>
                $"You write concise Danish CVs for a {role} position at {company}, grounded strictly in supplied evidence.{danishNameRule} Emphasize impact, technical judgment, and concrete achievements.",
            (OutputLanguage.Danish, DocumentKind.CoverLetter) =>
                $"You write direct Danish cover letters for a {role} position at {company}, using only supplied evidence.{danishNameRule} Keep the tone credible, practical, and specific to the target employer and role.",
            (OutputLanguage.Danish, DocumentKind.ProfileSummary) =>
                $"You write short Danish profile summaries tailored toward a {role} position at {company}, using only supplied evidence.{danishNameRule}",
            (OutputLanguage.Danish, DocumentKind.InterviewNotes) =>
                $"You prepare Danish interview notes for a {role} position at {company}, grounded in supplied evidence.{danishNameRule}",
            (OutputLanguage.Danish, _) =>
                $"You write high quality Danish application material for a {role} position at {company}, using only supplied evidence.{danishNameRule}",

            (_, DocumentKind.Cv) =>
                $"You write concise English CVs for a {role} position at {company}, grounded strictly in supplied evidence. Emphasize impact, technical judgment, and concrete achievements. Use clear headings and compact bullet points when useful.",
            (_, DocumentKind.CoverLetter) =>
                $"You write direct English cover letters for a {role} position at {company}, using only supplied evidence. Keep the tone credible, practical, and specific to the target employer and role.",
            (_, DocumentKind.ProfileSummary) =>
                $"You write short English profile summaries tailored toward a {role} position at {company}, using only supplied evidence. Keep them crisp, concrete, and senior without hype.",
            (_, DocumentKind.InterviewNotes) =>
                $"You prepare English interview notes for a {role} position at {company}, grounded in supplied evidence. Focus on likely themes, proof points, and talking angles tied to the job and company context.",
            _ =>
                $"You write high quality English application material for a {role} position at {company}, using only supplied evidence."
        };
    }

    private static string BuildUserPrompt(DocumentKind kind, DraftGenerationRequest request)
    {
        var outputLanguage = request.OutputLanguage;
        var languageLabel = outputLanguage == OutputLanguage.Danish ? "Danish" : "English";
        var candidate = request.Candidate;
        var experienceEntries = candidate.Experience.Take(8).ToArray();
        var experienceTruncationNote = candidate.Experience.Count > 8
            ? $"{Environment.NewLine}(Note: only the 8 most recent roles are included; {candidate.Experience.Count - 8} earlier role(s) are omitted.)"
            : string.Empty;
        var experience = string.Join(Environment.NewLine, experienceEntries.Select(FormatExperience)) + experienceTruncationNote;
        var skills = string.Join(", ", candidate.Skills.Take(20).Select(static skill => skill.Name));
        var certifications = string.Join(", ", candidate.Certifications.Select(static cert => cert.Name));
        var recommendations = string.Join(Environment.NewLine + Environment.NewLine, candidate.Recommendations.Take(3).Select(static recommendation =>
            $"Recommendation from {recommendation.Author.FullName}{FormatOptional($" at {recommendation.Company}")}: {recommendation.Text}"));
        var fitSummary = BuildFitSummary(request.JobFitAssessment);
        var differentiators = FormatLines(request.ApplicantDifferentiatorProfile?.ToSummaryLines(), "- No applicant differentiator profile is stored for this session.");
        var selectedEvidence = FormatSelectedEvidence(request.EvidenceSelection);

        return $"""
Generate a {kind} in {languageLabel}.

Hard constraints:
- Use only facts explicitly present in the evidence below.
- Do not invent employers, dates, certifications, tools, responsibilities, client names, metrics, team sizes, or outcomes.
- If a fact is missing or ambiguous, omit it instead of guessing.
- Prefer shorter output over speculative output.
- Do not claim experience with a technology unless it appears in the candidate evidence.
- Keep technology names, company names, and quoted job phrases in their original form.
- Do not mention gaps, weaknesses, missing skills, or any negative traits of the applicant.
- Do not include fit scores, fit percentages, gap lists, or internal assessment data in the output.
- Use the job themes, technology context, and fit review below only to guide emphasis and framing—never surface them directly.

Target role:
- Role title: {request.JobPosting.RoleTitle}
- Company: {request.JobPosting.CompanyName}
- Job summary: {request.JobPosting.Summary}
- Must-have themes: {FormatThemes(request.JobPosting.MustHaveThemes)}
- Nice-to-have themes: {FormatThemes(request.JobPosting.NiceToHaveThemes)}

Job fit review:
{fitSummary}

Candidate:
- Name: {candidate.Name.FullName}
- Headline: {candidate.Headline}
- Summary: {candidate.Summary}
- Location: {candidate.Location}
- Industry: {candidate.Industry}
- Skills: {skills}
- Certifications: {certifications}

Experience:
{experience}

Company context:
{request.CompanyContext}

Applicant differentiators:
{differentiators}

Selected evidence for this role:
{selectedEvidence}

Technology context:
{BuildTechnologyContext(request.TechnologyGapAssessment)}

Selected recommendations:
{recommendations}

Additional instructions:
{request.AdditionalInstructions}

Requirements:
- Write in {languageLabel}.
- Make the content concrete and evidence-based.
- Match the tone and focus to the target role and company context.
- Avoid generic buzzword-heavy wording.
- If evidence is thin, keep sections compact and factual.
- Do not add placeholder claims or inferred achievements.
- Avoid repeating the same fact, achievement, or phrasing in multiple sections.
""";
    }

    private static string BuildFitSummary(JobFitAssessment? assessment)
    {
        if (assessment is null || !assessment.HasSignals)
        {
            return "- No structured fit review is available for this job yet.";
        }

        var lines = new List<string>
        {
            $"- Overall fit score: {assessment.OverallScore}/100 ({assessment.Recommendation})"
        };

        lines.AddRange(assessment.Strengths.Take(4).Select(static strength => $"- Strength: {strength}"));
        lines.AddRange(assessment.Gaps.Take(3).Select(static gap => $"- Gap to frame around: {gap}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatThemes(IReadOnlyList<string> themes)
        => themes.Count > 0 ? string.Join(", ", themes) : "none identified";

    private static string BuildTechnologyContext(TechnologyGapAssessment? assessment)
    {
        if (assessment is null || !assessment.HasSignals)
        {
            return "- No technology gap analysis is available.";
        }

        var lines = new List<string>
        {
            $"- Key technologies the role emphasizes: {string.Join(", ", assessment.DetectedTechnologies)}"
        };

        if (assessment.HasGaps)
        {
            lines.Add($"- Technologies to avoid over-claiming (thin evidence): {string.Join(", ", assessment.PossiblyUnderrepresentedTechnologies)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatLines(IReadOnlyList<string>? lines, string fallback)
        => lines is { Count: > 0 }
            ? string.Join(Environment.NewLine, lines.Select(static line => $"- {line}"))
            : fallback;

    private static string FormatSelectedEvidence(EvidenceSelectionResult? evidenceSelection)
    {
        var selectedEvidence = evidenceSelection?.SelectedEvidence ?? Array.Empty<RankedEvidenceItem>();
        if (selectedEvidence.Count == 0)
        {
            return "- No explicit evidence selection is available. Use the imported profile conservatively.";
        }

        return string.Join(
            Environment.NewLine,
            selectedEvidence.Select(static item =>
                $"- {item.Evidence.Title}: {item.Evidence.Summary} (Reasons: {string.Join(", ", item.Reasons)})"));
    }

    private static string FormatExperience(ExperienceEntry role)
    {
        var description = string.IsNullOrWhiteSpace(role.Description) ? string.Empty : $" Description: {role.Description}";
        return $"- {role.Title} at {role.CompanyName} ({role.Period.DisplayValue}).{description}";
    }

    private static string FormatOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value;
}
