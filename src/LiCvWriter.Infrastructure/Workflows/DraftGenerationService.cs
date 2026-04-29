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

            string? generatedBody;
            IReadOnlyList<CvSectionContent>? generatedSections = null;
            LlmResponse response;

            if (kind == DocumentKind.Cv)
            {
                (generatedSections, response) = await GenerateCvSectionsAsync(
                    request,
                    selectedModel,
                    selectedThinkingLevel,
                    outputLanguage,
                    progress,
                    cancellationToken);
                generatedBody = null;
            }
            else
            {
                var systemPrompt = GetSystemPrompt(kind, outputLanguage, request.JobPosting);
                var userPrompt = BuildUserPrompt(kind, request);

                response = await llmClient.GenerateAsync(new LlmRequest(
                    selectedModel,
                    systemPrompt,
                    [new LlmChatMessage("user", userPrompt)],
                    UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                    Stream: true,
                    Think: selectedThinkingLevel,
                    KeepAlive: ollamaOptions.KeepAlive,
                    Temperature: ollamaOptions.Temperature,
                    PromptId: LlmPromptCatalog.DraftDocumentMarkdown,
                    PromptVersion: LlmPromptCatalog.Version1),
                    progress is null ? null : update => progress(PrefixProgress(kind, update)),
                    cancellationToken);

                generatedBody = response.Content;
            }

            var renderedDocument = await documentRenderer.RenderAsync(new DocumentRenderRequest(
                kind,
                request.Candidate,
                request.JobPosting,
                request.CompanyContext,
                generatedBody,
                outputLanguage,
                request.JobFitAssessment,
                request.ApplicantDifferentiatorProfile,
                request.EvidenceSelection,
                request.TechnologyGapAssessment,
                generatedSections,
                request.PersonalContact), cancellationToken);

            var document = (string.IsNullOrWhiteSpace(request.ExportFolder)
                ? renderedDocument
                : renderedDocument with { OutputPath = request.ExportFolder.Trim() }) with
            {
                LlmDuration = response.Duration,
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens,
                Model = response.Model,
                GeneratedSections = generatedSections is null
                    ? null
                    : generatedSections.Select(s => new CvSectionMarkdown(s.Section, s.Markdown)).ToList()
            };

            document = ApplicationMaterialLengthPolicy.Enforce(document);

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
                    ["FilePath"] = export?.FilePath ?? string.Empty,
                    ["CvMissingMustHaveThemeCount"] = cvQualityResult.Report.MissingMustHaveThemeCount.ToString(),
                    ["CvQuantifiedBulletCount"] = cvQualityResult.Report.QuantifiedBulletCount.ToString(),
                    ["CvSummaryTrimmed"] = cvQualityResult.Report.SummaryTrimmed.ToString(),
                    ["CvSectionOrderChanged"] = cvQualityResult.Report.SectionOrderChanged.ToString(),
                    ["CvTrimmedOptionalSections"] = cvQualityResult.Report.TrimmedOptionalSections.Count == 0
                        ? string.Empty
                        : string.Join(",", cvQualityResult.Report.TrimmedOptionalSections),
                    ["CvAppliedFixes"] = cvQualityResult.Report.AppliedFixes.Count == 0
                        ? string.Empty
                        : string.Join(",", cvQualityResult.Report.AppliedFixes),
                    ["CvAtsKeywordCoveragePercent"] = cvQualityResult.Report.AtsKeywordCoveragePercent.ToString(),
                    ["CvMissingMustHaveThemes"] = cvQualityResult.Report.MissingMustHaveThemes is { Count: > 0 }
                        ? string.Join(",", cvQualityResult.Report.MissingMustHaveThemes)
                        : string.Empty,
                    ["CvActionableFeedback"] = string.Join(" | ", cvQualityResult.Report.BuildActionableFeedback())
                }), cancellationToken);

            documentStopwatch.Stop();
            var qualityFeedback = cvQualityResult.Report.BuildActionableFeedback();
            var completionDetail = qualityFeedback.Count > 0
                ? $"{kind} finished in {FormatDuration(documentStopwatch.Elapsed)} (LLM: {FormatDuration(response.Duration)}). Quality: {string.Join(" ", qualityFeedback)}"
                : $"{kind} finished in {FormatDuration(documentStopwatch.Elapsed)} (LLM: {FormatDuration(response.Duration)}).";
            progress?.Invoke(new LlmProgressUpdate(
                $"Completed {kind}",
                completionDetail,
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
                $"Write a concise {lang} CV for a {role} position at {company}, grounded strictly in supplied evidence.{nameRule} Emphasize impact, technical judgment, and concrete achievements. Weave as many of the job's key technologies and themes into the professional profile as truthfully possible. Keep the CV within four pages and do not include recommendations; those are generated as a separate document.",
            DocumentKind.CoverLetter =>
                $"""
                Write a direct {lang} cover letter for a {role} position at {company}, using only supplied evidence.{nameRule}

                Keep it very focused and no more than one page. Aim for 250-350 words total.
                Structure the letter in 3-4 compact paragraphs: opening fit, strongest evidence for the role, company/culture alignment, and a confident close.
                Use concrete outcomes where available, but do not include appendices, headings, fit scores, gap lists, or evidence tables.
                Keep the tone credible, practical, and specific to the target employer and role.
                """,
            DocumentKind.ProfileSummary =>
                $"Write a very focused {lang} profile summary tailored toward a {role} position at {company}, using only supplied evidence.{nameRule} Keep it to one page or less, ideally 120-180 words. Make it crisp, concrete, senior, and free of hype. Do not include fit scores, evidence tables, or appendices.",
            DocumentKind.Recommendations =>
                $"Write a compact {lang} recommendation brief for a {role} position at {company}, using only supplied recommendation evidence.{nameRule} Produce 1-2 short paragraphs that introduce the strongest credibility themes. Do not invent, rewrite, translate, or quote recommendation text; the renderer will append the original recommendations with attribution.",
            DocumentKind.InterviewNotes =>
                $"""
                Prepare focused {lang} interview questions for a {role} position at {company}, grounded in supplied evidence.{nameRule}

                Keep it no more than one page. Produce 6-8 questions total, grouped under compact headings if useful.
                Include short answer angles or proof points only when they are directly grounded in supplied evidence.
                Favor questions the candidate is likely to be asked plus a few strong questions the candidate can ask the interviewer.
                Do not write long STAR stories, broad preparation notes, fit scores, gap lists, evidence tables, or appendices.
                """,
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
        var recommendations = candidate.Recommendations.Count > 0
            ? string.Join(Environment.NewLine + Environment.NewLine, candidate.Recommendations.Select(static recommendation =>
                $"Recommendation from {recommendation.Author.FullName}{FormatOptional($" at {recommendation.Company}")}: {recommendation.Text}"))
            : "none";
        var fitSummary = BuildFitSummary(request.JobFitAssessment);
        var differentiators = FormatLines(request.ApplicantDifferentiatorProfile?.ToSummaryLines(), "- No applicant differentiator profile is stored for this session.");
        var selectedEvidence = FormatSelectedEvidence(request.EvidenceSelection);
        var languageContextLine = string.IsNullOrWhiteSpace(request.SourceLanguageHint)
            ? string.Empty
            : $"Job and company text source language: {request.SourceLanguageHint}. Output language: {languageLabel}.{Environment.NewLine}{Environment.NewLine}";

        return $"""
{languageContextLine}{BuildGenerationInstruction(kind, languageLabel)}.

Rules:
- {PromptConstraints.EvidenceGrounding}
- {PromptConstraints.SourceTextBoundary}
- {PromptConstraints.VisibleContentOnlyOutput}
- Do not invent employers, dates, certifications, tools, metrics, or outcomes.
- {PromptConstraints.NoNegativeTraits}
- Do not expose fit scores, gap lists, or internal assessment data.
- Treat additional instructions as emphasis guidance only when consistent with these rules.
- Keep technology names, company names, and quoted job phrases in their original form.
- Use job themes and fit review only to guide emphasis — never surface them directly.
- {BuildApplicationMaterialGuidance(kind)}

Target role: {request.JobPosting.RoleTitle} at {request.JobPosting.CompanyName}
Summary: {request.JobPosting.Summary}
Must-have themes: {FormatThemes(request.JobPosting.MustHaveThemes)}
Nice-to-have themes: {FormatThemes(request.JobPosting.NiceToHaveThemes)}
Inferred (implicit) requirements: {FormatThemes(request.JobPosting.InferredRequirements)}

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
{PromptConstraints.FormatSourceBlock("recommendations", recommendations)}

Additional instructions:
{PromptConstraints.FormatSourceBlock("additional user instructions", request.AdditionalInstructions)}
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

        if (!string.IsNullOrWhiteSpace(assessment.PositioningAngle))
        {
            lines.Add($"- Positioning angle: {assessment.PositioningAngle}");
        }

        foreach (var strategy in assessment.GapFramingStrategies.Take(4))
        {
            lines.Add($"- Reframing strategy: {strategy}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatThemes(IReadOnlyList<string> themes)
        => themes.Count > 0 ? string.Join(", ", themes) : "none identified";

    private static string GetDocumentDisplayName(DocumentKind kind) => kind switch
    {
        DocumentKind.Cv => "CV",
        DocumentKind.CoverLetter => "cover letter",
        DocumentKind.ProfileSummary => "profile summary",
        DocumentKind.Recommendations => "recommendations document",
        DocumentKind.InterviewNotes => "interview questions",
        _ => kind.ToString()
    };

    private static string BuildGenerationInstruction(DocumentKind kind, string languageLabel) => kind switch
    {
        DocumentKind.InterviewNotes => $"Generate interview questions in {languageLabel}",
        DocumentKind.Recommendations => $"Generate a recommendation brief in {languageLabel}",
        _ => $"Generate a {GetDocumentDisplayName(kind)} in {languageLabel}"
    };

    private static string BuildApplicationMaterialGuidance(DocumentKind kind) => kind switch
    {
        DocumentKind.Cv => PromptConstraints.CvQualityGuidance,
        DocumentKind.CoverLetter => "Keep the cover letter very focused and no more than one page; target 250-350 words in 3-4 compact paragraphs.",
        DocumentKind.ProfileSummary => "Keep the profile summary very focused and no more than one page; target 120-180 words.",
        DocumentKind.Recommendations => "Keep the recommendation brief very focused; target 120-180 words and let the deterministic recommendation section carry the full quotes.",
        DocumentKind.InterviewNotes => "Keep the interview questions very focused and no more than one page; produce 6-8 grounded questions with brief answer angles.",
        _ => "Keep the output focused, grounded, and concise."
    };

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

    private static string FormatCoveredProjects(CandidateProfile candidate)
    {
        var covered = GetCoveredProjects(candidate);
        if (covered.Count == 0)
        {
            return string.Empty;
        }

        var lines = string.Join(
            Environment.NewLine,
            covered.Select(static project =>
                $"- {project.Title} ({project.Period.DisplayValue}). {project.Description}"));

        return $"""

Client engagements during consulting/freelance roles (list the most relevant 3-5 as sub-bullets under that role; omit the rest):
{lines}

""";
    }

    private static IEnumerable<ExperienceEntry> GetModernExperience(CandidateProfile candidate)
        => candidate.Experience.Where(static role => !IsBeforeCutoff(role.Period));

    private static IEnumerable<ProjectEntry> GetModernProjects(CandidateProfile candidate)
        => candidate.Projects.Where(static project => !IsBeforeCutoff(project.Period));

    /// <summary>
    /// Returns modern projects that are NOT sub-engagements of an umbrella
    /// consulting/freelance role. A role is considered an umbrella role when
    /// it covers 3+ projects within its date range.
    /// </summary>
    private static IReadOnlyList<ProjectEntry> GetStandaloneProjects(CandidateProfile candidate)
    {
        var modernExperience = GetModernExperience(candidate).ToArray();
        var modernProjects = GetModernProjects(candidate).ToArray();
        var umbrellaRoles = modernExperience
            .Where(exp => modernProjects.Count(p => PeriodContains(exp.Period, p.Period)) >= 3)
            .ToArray();
        return modernProjects
            .Where(project => !umbrellaRoles.Any(exp => PeriodContains(exp.Period, project.Period)))
            .ToList();
    }

    /// <summary>
    /// Returns modern projects whose period IS contained within an umbrella
    /// consulting/freelance role (3+ covered projects).
    /// </summary>
    private static IReadOnlyList<ProjectEntry> GetCoveredProjects(CandidateProfile candidate)
    {
        var modernExperience = GetModernExperience(candidate).ToArray();
        var modernProjects = GetModernProjects(candidate).ToArray();
        var umbrellaRoles = modernExperience
            .Where(exp => modernProjects.Count(p => PeriodContains(exp.Period, p.Period)) >= 3)
            .ToArray();
        return modernProjects
            .Where(project => umbrellaRoles.Any(exp => PeriodContains(exp.Period, project.Period)))
            .ToList();
    }

    private static bool PeriodContains(DateRange outer, DateRange inner)
    {
        var outerStartYear = outer.StartedOn?.Year;
        var outerEndYear = outer.FinishedOn?.Year ?? 9999;
        var innerStartYear = inner.StartedOn?.Year;
        if (outerStartYear is null || innerStartYear is null) return false;
        var innerEndYear = inner.FinishedOn?.Year ?? innerStartYear.Value;
        return innerStartYear >= outerStartYear && innerEndYear <= outerEndYear;
    }

    private static bool IsBeforeCutoff(DateRange period)
    {
        var year = period.StartedOn?.Year ?? period.FinishedOn?.Year;
        return year is not null && year.Value < EarlyCareerCutoffYear;
    }

    private static string FormatOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value;

    private async Task<(IReadOnlyList<CvSectionContent> Sections, LlmResponse Aggregate)> GenerateCvSectionsAsync(
        DraftGenerationRequest request,
        string selectedModel,
        string selectedThinkingLevel,
        OutputLanguage outputLanguage,
        Action<LlmProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var sections = new List<CvSectionContent>();
        var totalDuration = TimeSpan.Zero;
        long totalPromptTokens = 0;
        long totalCompletionTokens = 0;
        var lastModel = selectedModel;
        var lastThinking = string.Empty;

        var hasProjects = GetStandaloneProjects(request.Candidate).Count > 0;

        // Wave 1: ProfileSummary + KeySkills are independent — generate in parallel.
        var wave1 = new[] { CvSection.ProfileSummary, CvSection.KeySkills };
        var wave1Results = await GenerateSectionWaveAsync(wave1, request, selectedModel, selectedThinkingLevel, outputLanguage, progress, cancellationToken);

        // Wave 2: ExperienceHighlights + ProjectHighlights are independent — generate in parallel.
        var wave2 = hasProjects
            ? new[] { CvSection.ExperienceHighlights, CvSection.ProjectHighlights }
            : new[] { CvSection.ExperienceHighlights };
        var wave2Results = await GenerateSectionWaveAsync(wave2, request, selectedModel, selectedThinkingLevel, outputLanguage, progress, cancellationToken);

        // Two-pass refinement: review ExperienceHighlights against must-have themes and improve.
        var experienceResult = wave2Results.FirstOrDefault(r => r.Content.Section == CvSection.ExperienceHighlights);
        if (experienceResult.Content is not null && request.JobPosting.MustHaveThemes.Count > 0)
        {
            var refined = await RefineExperienceHighlightsAsync(
                experienceResult.Content.Markdown,
                request,
                selectedModel,
                selectedThinkingLevel,
                outputLanguage,
                progress,
                cancellationToken);

            if (refined is not null)
            {
                var refinedValue = refined.Value;
                wave2Results = wave2Results
                    .Select(r => r.Content.Section == CvSection.ExperienceHighlights
                        ? (r.Content with { Markdown = refinedValue.Content }, refinedValue.Response)
                        : r)
                    .ToArray();
            }
        }

        foreach (var (content, response) in wave1Results.Concat(wave2Results))
        {
            sections.Add(content);

            if (response.Duration is { } d)
            {
                totalDuration += d;
            }

            totalPromptTokens += response.PromptTokens ?? 0;
            totalCompletionTokens += response.CompletionTokens ?? 0;
            lastModel = response.Model;
            lastThinking = response.Thinking ?? lastThinking;
        }

        var combinedContent = string.Join(
            Environment.NewLine + Environment.NewLine,
            sections.Select(s => $"<!-- {s.Section} -->{Environment.NewLine}{s.Markdown}"));

        var aggregate = new LlmResponse(
            lastModel,
            combinedContent,
            string.IsNullOrWhiteSpace(lastThinking) ? null : lastThinking,
            Completed: true,
            PromptTokens: totalPromptTokens > 0 ? totalPromptTokens : null,
            CompletionTokens: totalCompletionTokens > 0 ? totalCompletionTokens : null,
            Duration: totalDuration > TimeSpan.Zero ? totalDuration : null);

        return (sections, aggregate);
    }

    /// <summary>
    /// Generates multiple CV sections in parallel using <see cref="Task.WhenAll"/>.
    /// Sections within a wave are independent and can safely run concurrently.
    /// </summary>
    private async Task<IReadOnlyList<(CvSectionContent Content, LlmResponse Response)>> GenerateSectionWaveAsync(
        IReadOnlyList<CvSection> sections,
        DraftGenerationRequest request,
        string selectedModel,
        string selectedThinkingLevel,
        OutputLanguage outputLanguage,
        Action<LlmProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var tasks = sections.Select(async section =>
        {
            var systemPrompt = GetCvSectionSystemPrompt(section, outputLanguage, request.JobPosting);
            var userPrompt = BuildCvSectionUserPrompt(section, request);
            var sectionLabel = GetCvSectionLabel(section, outputLanguage);

            var sectionResponse = await llmClient.GenerateAsync(new LlmRequest(
                selectedModel,
                systemPrompt,
                [new LlmChatMessage("user", userPrompt)],
                UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                Stream: true,
                Think: selectedThinkingLevel,
                KeepAlive: ollamaOptions.KeepAlive,
                Temperature: ollamaOptions.Temperature,
                PromptId: LlmPromptCatalog.CvSectionsMarkdown,
                PromptVersion: LlmPromptCatalog.Version1),
                progress is null ? null : update => progress(PrefixCvSectionProgress(sectionLabel, update)),
                cancellationToken);

            var content = new CvSectionContent(
                section,
                sectionResponse.Content.Trim(),
                sectionResponse.Duration,
                sectionResponse.PromptTokens,
                sectionResponse.CompletionTokens);

            return (Content: content, Response: sectionResponse);
        });

        var results = await Task.WhenAll(tasks);
        return results;
    }

    /// <summary>
    /// Second-pass refinement: reviews generated experience bullets against the must-have themes
    /// and asks the LLM to improve coverage for themes not yet addressed.
    /// Returns null if no refinement is needed or if the pass fails.
    /// </summary>
    private async Task<(string Content, LlmResponse Response)?> RefineExperienceHighlightsAsync(
        string firstPassMarkdown,
        DraftGenerationRequest request,
        string selectedModel,
        string selectedThinkingLevel,
        OutputLanguage outputLanguage,
        Action<LlmProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var mustHaveThemes = request.JobPosting.MustHaveThemes
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .ToArray();

        var missingThemes = mustHaveThemes
            .Where(theme => !firstPassMarkdown.Contains(theme, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (missingThemes.Length == 0)
        {
            return null;
        }

        var lang = outputLanguage == OutputLanguage.Danish ? "Danish" : "English";
        var systemPrompt =
            $"You are a CV refinement editor. Review the candidate's experience bullets against the job's must-have themes. " +
            $"For each theme not yet addressed, improve or replace the weakest bullet to incorporate it — but only if the candidate has evidence for it. " +
            $"{PromptConstraints.SourceTextBoundary} {PromptConstraints.VisibleContentOnlyOutput} " +
            $"For bullets that could be more specific or quantified, add concrete metrics (percentages, counts, timeframes, scale) where the evidence supports it. " +
            $"Prefer action-result format: 'Led X, resulting in Y' or 'Delivered X across Y, achieving Z'. " +
            $"Output the complete refined {lang} experience section in markdown. " +
            $"Preserve exact role titles, companies, and date periods. Output {lang} markdown only with no preamble, no closing remarks, and no fenced code blocks.";

        var userPrompt = $"""
Must-have themes NOT yet covered in the experience section: {string.Join(", ", missingThemes)}
All must-have themes: {string.Join(", ", mustHaveThemes)}

Current experience section (refine this):
{firstPassMarkdown}

Selected evidence (use for grounding only):
{FormatSelectedEvidence(request.EvidenceSelection)}

Rules:
- {PromptConstraints.SourceTextBoundary}
- {PromptConstraints.VisibleContentOnlyOutput}
- Only add theme keywords if the candidate has genuine evidence for them.
- Do not invent employers, dates, certifications, tools, metrics, or outcomes.
- Preserve the exact ### heading format for each role.
- Each `-` must start a new line.
""";

        var sectionLabel = outputLanguage == OutputLanguage.Danish ? "Erhvervserfaring (raffinering)" : "Experience (refinement)";

        try
        {
            var response = await llmClient.GenerateAsync(new LlmRequest(
                selectedModel,
                systemPrompt,
                [new LlmChatMessage("user", userPrompt)],
                UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                Stream: true,
                Think: selectedThinkingLevel,
                KeepAlive: ollamaOptions.KeepAlive,
                Temperature: ollamaOptions.Temperature,
                PromptId: LlmPromptCatalog.CvRefineMarkdown,
                PromptVersion: LlmPromptCatalog.Version1),
                progress is null ? null : update => progress(PrefixCvSectionProgress(sectionLabel, update)),
                cancellationToken);

            var refined = response.Content.Trim();
            if (string.IsNullOrWhiteSpace(refined) || refined.Length < firstPassMarkdown.Length / 2)
            {
                return null;
            }

            return (refined, response);
        }
        catch
        {
            return null;
        }
    }

    private static LlmProgressUpdate PrefixCvSectionProgress(string sectionLabel, LlmProgressUpdate update)
        => update with
        {
            Message = $"Generating CV — {sectionLabel}",
            Detail = string.IsNullOrWhiteSpace(update.Detail)
                ? $"{sectionLabel} is streaming from {update.Model}."
                : $"{sectionLabel}: {update.Detail}"
        };

    private static string GetCvSectionLabel(CvSection section, OutputLanguage outputLanguage)
        => (section, outputLanguage) switch
        {
            (CvSection.ProfileSummary, OutputLanguage.Danish) => "Professionel profil",
            (CvSection.KeySkills, OutputLanguage.Danish) => "Nøgleteknologier",
            (CvSection.ExperienceHighlights, OutputLanguage.Danish) => "Erhvervserfaring",
            (CvSection.ProjectHighlights, OutputLanguage.Danish) => "Projekter",
            (CvSection.ProfileSummary, _) => "Professional Profile",
            (CvSection.KeySkills, _) => "Key Skills",
            (CvSection.ExperienceHighlights, _) => "Experience",
            (CvSection.ProjectHighlights, _) => "Projects",
            _ => section.ToString()
        };

    private static string GetCvSectionSystemPrompt(CvSection section, OutputLanguage outputLanguage, JobPostingAnalysis jobPosting)
    {
        var lang = outputLanguage == OutputLanguage.Danish ? "Danish" : "English";
        var role = jobPosting.RoleTitle;
        var company = jobPosting.CompanyName;
        var nameRule = outputLanguage == OutputLanguage.Danish
            ? " Keep technology names, company names, quoted job phrases, and file names in their original or English form."
            : string.Empty;
        var commonRules =
            $" {PromptConstraints.SourceTextBoundary} {PromptConstraints.VisibleContentOnlyOutput} Use only the supplied evidence — do not invent employers, dates, certifications, tools, metrics, or outcomes.{nameRule} Output {lang} markdown only with no preamble, no closing remarks, and no fenced code blocks. Use `-` (hyphen + space) for every bullet, never `*`. Put each bullet on its own line.";

        return section switch
        {
            CvSection.ProfileSummary =>
                $"Write a 3-4 line {lang} professional profile paragraph positioning the candidate for a {role} role at {company}. Lead with seniority and domain, fold in 2-3 of the role's key technologies/themes truthfully, and emphasize impact over duties.{commonRules}",
            CvSection.KeySkills =>
                $"Produce a single comma-separated keyword line of {lang} technologies and competencies tailored to the {role} role at {company}. Order keywords by relevance to the role's must-have themes; include only items the candidate has evidence for. No headings, no bullets — just the comma-separated line. ATS tip: ensure every must-have theme the candidate has evidence for appears verbatim in the output.{commonRules}",
            CvSection.ExperienceHighlights =>
                $"Rewrite the candidate's role descriptions as achievement-focused {lang} bullets for a {role} position at {company}.{commonRules}\n\nFor each role use exactly this layout, with one item per line and a blank line between roles:\n\n### {{Title}} | {{Company}} | {{Period}}\n\n- {{Achievement bullet starting with a strong verb}}\n- {{Another achievement bullet}}\n\nAim for 2-4 bullets per role. Where truthful, weave must-have theme keywords into bullet text for ATS matching. If client engagements are provided for a consulting/freelance role, list the 3-5 most relevant engagements as bullets with a bold 'Client:' prefix, e.g.:\n- **Client: {{Client name}}** — {{description}} ({{period}})\nDo not create a separate section for them. Do not write the bullets inline; each `-` must start a new line.",
            CvSection.ProjectHighlights =>
                $"Rewrite the candidate's project descriptions as achievement-focused {lang} bullets relevant to the {role} role at {company}.{commonRules}\n\nFor each project use exactly this layout, with one item per line and a blank line between projects:\n\n### {{Title}} | {{Period}}\n\n- {{Outcome bullet}}\n- {{Outcome bullet}}\n\nAim for 2-3 bullets per project. Do not write the bullets inline; each `-` must start a new line.",
            _ => GetSystemPrompt(DocumentKind.Cv, outputLanguage, jobPosting)
        };
    }

    private const int EarlyCareerCutoffYear = 2008;

    private static string BuildCvSectionUserPrompt(CvSection section, DraftGenerationRequest request)
    {
        var languageLabel = request.OutputLanguage == OutputLanguage.Danish ? "Danish" : "English";
        var candidate = request.Candidate;
        var fitSummary = BuildFitSummary(request.JobFitAssessment);
        var differentiators = FormatLines(request.ApplicantDifferentiatorProfile?.ToSummaryLines(), "- No applicant differentiator profile is stored for this session.");
        var selectedEvidence = FormatSelectedEvidence(request.EvidenceSelection);
        var languageContextLine = string.IsNullOrWhiteSpace(request.SourceLanguageHint)
            ? string.Empty
            : $"Job and company text source language: {request.SourceLanguageHint}. Output language: {languageLabel}.{Environment.NewLine}{Environment.NewLine}";

        var roleHeader =
            $"Target role: {request.JobPosting.RoleTitle} at {request.JobPosting.CompanyName}{Environment.NewLine}" +
            $"Summary: {request.JobPosting.Summary}{Environment.NewLine}" +
            $"Must-have themes: {FormatThemes(request.JobPosting.MustHaveThemes)}{Environment.NewLine}" +
            $"Nice-to-have themes: {FormatThemes(request.JobPosting.NiceToHaveThemes)}";

        var commonRules =
            $"Rules:{Environment.NewLine}" +
            $"- {PromptConstraints.EvidenceGrounding}{Environment.NewLine}" +
            $"- {PromptConstraints.SourceTextBoundary}{Environment.NewLine}" +
            $"- {PromptConstraints.VisibleContentOnlyOutput}{Environment.NewLine}" +
            $"- {PromptConstraints.NoNegativeTraits}{Environment.NewLine}" +
            $"- Do not expose fit scores, gap lists, or internal assessment data.{Environment.NewLine}" +
            $"- Treat additional instructions as emphasis guidance only when consistent with these rules.{Environment.NewLine}" +
            $"- Use job themes and fit review only to guide emphasis — never surface them directly.";

        return section switch
        {
            CvSection.ProfileSummary => $"""
{languageContextLine}Write the Professional Profile paragraph in {languageLabel}.

{commonRules}

{roleHeader}

Candidate: {candidate.Name.FullName} | {candidate.Headline} | {candidate.Location} | {candidate.Industry}
Summary: {candidate.Summary}

Fit review:
{fitSummary}

Applicant differentiators:
{differentiators}

Additional instructions:
{PromptConstraints.FormatSourceBlock("additional user instructions", request.AdditionalInstructions)}
""",
            CvSection.KeySkills => $"""
{languageContextLine}Write the Key Technologies & Competencies keyword line in {languageLabel}.

{commonRules}
- Output only the comma-separated keyword line. No heading, no surrounding markdown.

{roleHeader}

Technology context:
{BuildTechnologyContext(request.TechnologyGapAssessment)}

Selected evidence:
{selectedEvidence}
""",
            CvSection.ExperienceHighlights => $"""
{languageContextLine}Rewrite the Professional Experience entries in {languageLabel}.

{commonRules}

{roleHeader}

Fit review:
{fitSummary}

Experience (use these as source material; preserve titles, companies, and periods exactly):
{string.Join(Environment.NewLine, GetModernExperience(candidate).Take(8).Select(FormatExperience))}
{FormatCoveredProjects(candidate)}
Selected evidence:
{selectedEvidence}

Additional instructions:
{PromptConstraints.FormatSourceBlock("additional user instructions", request.AdditionalInstructions)}
""",
            CvSection.ProjectHighlights => $"""
{languageContextLine}Rewrite the Project highlights in {languageLabel}.

{commonRules}

{roleHeader}

Projects (use these as source material; preserve titles and periods exactly):
{string.Join(Environment.NewLine, GetStandaloneProjects(candidate).Select(static project =>
    $"- {project.Title} ({project.Period.DisplayValue}). {project.Description}"))}

Selected evidence:
{selectedEvidence}
""",
            _ => BuildUserPrompt(DocumentKind.Cv, request)
        };
    }
}
