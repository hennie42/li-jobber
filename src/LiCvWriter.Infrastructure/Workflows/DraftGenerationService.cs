using System.Diagnostics;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Auditing;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Infrastructure.Documents;

namespace LiCvWriter.Infrastructure.Workflows;

public sealed class DraftGenerationService(
    ILlmClient llmClient,
    IDocumentRenderer documentRenderer,
    IDocumentExportService documentExportService,
    IAuditStore auditStore,
    CvQualityValidator cvQualityValidator,
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
                request.EvidenceSelection,
                request.TechnologyGapAssessment), cancellationToken);

            var document = (string.IsNullOrWhiteSpace(request.ExportFolder)
                ? renderedDocument
                : renderedDocument with { OutputPath = request.ExportFolder.Trim() }) with
            {
                LlmDuration = response.Duration,
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens,
                Model = response.Model
            };

            var cvQualityResult = cvQualityValidator.ValidateAndAutoFix(document, request);
            document = cvQualityResult.Document;

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
                    ["MarkdownPath"] = export?.MarkdownPath ?? string.Empty,
                    ["CvMissingMustHaveThemes"] = cvQualityResult.Report.MissingMustHaveThemeCount.ToString(),
                    ["CvQuantifiedBulletCount"] = cvQualityResult.Report.QuantifiedBulletCount.ToString(),
                    ["CvSummaryTrimmed"] = cvQualityResult.Report.SummaryTrimmed.ToString(),
                    ["CvSectionOrderChanged"] = cvQualityResult.Report.SectionOrderChanged.ToString(),
                    ["CvTrimmedOptionalSections"] = cvQualityResult.Report.TrimmedOptionalSections.Count == 0
                        ? string.Empty
                        : string.Join(",", cvQualityResult.Report.TrimmedOptionalSections),
                    ["CvAppliedFixes"] = cvQualityResult.Report.AppliedFixes.Count == 0
                        ? string.Empty
                        : string.Join(",", cvQualityResult.Report.AppliedFixes)
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
        var lang = outputLanguage == OutputLanguage.Danish ? "Danish" : "English";
        var role = jobPosting.RoleTitle;
        var company = jobPosting.CompanyName;
        var nameRule = outputLanguage == OutputLanguage.Danish
            ? " Keep technology names, company names, quoted job phrases, and file names in their original or English form."
            : string.Empty;

        var focus = kind switch
        {
            DocumentKind.Cv =>
                $"Write a concise {lang} CV for a {role} position at {company}, grounded strictly in supplied evidence.{nameRule} Emphasize impact, technical judgment, and concrete achievements. Weave as many of the job's key technologies and themes into the professional profile as truthfully possible. If any recommendation text is not in {lang}, translate it to {lang} and append '(translated from <original language>)' after the translated text.",
            DocumentKind.CoverLetter =>
                $"Write a direct {lang} cover letter for a {role} position at {company}, using only supplied evidence.{nameRule} Keep the tone credible, practical, and specific to the target employer and role.",
            DocumentKind.ProfileSummary =>
                $"Write a short {lang} profile summary tailored toward a {role} position at {company}, using only supplied evidence.{nameRule} Keep it crisp, concrete, and senior without hype.",
            DocumentKind.InterviewNotes =>
                $"Prepare {lang} interview notes for a {role} position at {company}, grounded in supplied evidence.{nameRule} Focus on likely themes, proof points, and talking angles tied to the job and company context.",
            _ =>
                $"Write high-quality {lang} application material for a {role} position at {company}, using only supplied evidence.{nameRule}"
        };

        return focus;
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
        var certifications = string.Join(", ", candidate.Certifications.Select(static cert => cert.Name));
        var projects = candidate.Projects.Count > 0
            ? string.Join(Environment.NewLine, candidate.Projects.Select(static project =>
                $"- {project.Title} ({project.Period.DisplayValue}). {project.Description}"))
            : "none";
        var recommendations = string.Join(Environment.NewLine + Environment.NewLine, candidate.Recommendations.Select(static recommendation =>
            $"Recommendation from {recommendation.Author.FullName}{FormatOptional($" at {recommendation.Company}")}: {recommendation.Text}"));
        var fitSummary = BuildFitSummary(request.JobFitAssessment);
        var differentiators = FormatLines(request.ApplicantDifferentiatorProfile?.ToSummaryLines(), "- No applicant differentiator profile is stored for this session.");
        var selectedEvidence = FormatSelectedEvidence(request.EvidenceSelection);
        var languageContextLine = string.IsNullOrWhiteSpace(request.SourceLanguageHint)
            ? string.Empty
            : $"Job and company text source language: {request.SourceLanguageHint}. Output language: {languageLabel}.{Environment.NewLine}{Environment.NewLine}";

        return $"""
{languageContextLine}Generate a {kind} in {languageLabel}.

Rules:
    - {PromptConstraints.EvidenceGrounding}
- Do not invent employers, dates, certifications, tools, metrics, or outcomes.
    - {PromptConstraints.NoNegativeTraits}
- Do not expose fit scores, gap lists, or internal assessment data.
- Keep technology names, company names, and quoted job phrases in their original form.
- Use job themes and fit review only to guide emphasis — never surface them directly.
    - {PromptConstraints.CvQualityGuidance}

Target role: {request.JobPosting.RoleTitle} at {request.JobPosting.CompanyName}
Summary: {request.JobPosting.Summary}
Must-have themes: {FormatThemes(request.JobPosting.MustHaveThemes)}
Nice-to-have themes: {FormatThemes(request.JobPosting.NiceToHaveThemes)}

Fit review:
{fitSummary}

Candidate: {candidate.Name.FullName} | {candidate.Headline} | {candidate.Location} | {candidate.Industry}
Summary: {candidate.Summary}
Certifications: {certifications}

Experience:
{experience}

Projects:
{projects}

Company context:
{request.CompanyContext}

Applicant differentiators:
{differentiators}

Selected evidence:
{selectedEvidence}

Technology context:
{BuildTechnologyContext(request.TechnologyGapAssessment)}

Recommendations:
{recommendations}

Additional instructions:
{request.AdditionalInstructions}
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
