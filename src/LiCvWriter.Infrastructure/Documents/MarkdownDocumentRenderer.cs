using System.Text;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Infrastructure.Documents;

/// <summary>
/// Renders <see cref="GeneratedDocument"/> instances as Markdown with ATS-friendly section titles.
/// </summary>
public sealed class MarkdownDocumentRenderer : IDocumentRenderer
{
    private const int EarlyCareerCutoffYear = 2009;

    /// <summary>Common Danish words used for language detection heuristic.</summary>
    private static readonly HashSet<string> DanishMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "og", "er", "med", "har", "det", "en", "af", "til", "som", "den",
        "for", "ikke", "på", "kan", "vil", "var", "blev", "fra", "meget",
        "også", "jeg", "vi", "hun", "han", "min", "hans", "hendes", "dette",
        "deres", "eller", "hvor", "når", "efter", "inden", "uden", "mellem"
    };

    public Task<GeneratedDocument> RenderAsync(DocumentRenderRequest request, CancellationToken cancellationToken = default)
    {
        var outputLanguage = request.OutputLanguage;
        var selectedEvidence = request.EvidenceSelection?.SelectedEvidence ?? Array.Empty<RankedEvidenceItem>();
        var builder = new StringBuilder();
        builder.AppendLine($"# {request.Candidate.Name.FullName}");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(request.Candidate.Headline))
        {
            builder.AppendLine($"> {request.Candidate.Headline}");
            builder.AppendLine();
        }

        AppendContactLine(builder, request.PersonalContact, request.Candidate);

        builder.AppendLine($"## {Translate(outputLanguage, "Target Role", "Målrolle")}");
        builder.AppendLine();
        builder.AppendLine($"- {Translate(outputLanguage, "Role", "Rolle")}: {request.JobPosting.RoleTitle}");
        builder.AppendLine($"- {Translate(outputLanguage, "Company", "Virksomhed")}: {request.JobPosting.CompanyName}");
        builder.AppendLine();

        var profileSection = FindSection(request.GeneratedSections, CvSection.ProfileSummary);
        var generatedBody = !string.IsNullOrWhiteSpace(profileSection)
            ? profileSection!.Trim()
            : !string.IsNullOrWhiteSpace(request.GeneratedBody)
                ? request.GeneratedBody.Trim()
                : request.Candidate.Summary?.Trim();

        switch (request.Kind)
        {
            case DocumentKind.Cv:
                var modernExperience = request.Candidate.Experience
                    .Where(static role => !IsBeforeCutoff(role.Period, EarlyCareerCutoffYear))
                    .ToArray();
                var earlyCareerExperience = request.Candidate.Experience
                    .Where(static role => IsBeforeCutoff(role.Period, EarlyCareerCutoffYear))
                    .ToArray();
                var modernProjects = request.Candidate.Projects
                    .Where(static project => !IsBeforeCutoff(project.Period, EarlyCareerCutoffYear))
                    .ToArray();
                var earlyCareerProjects = request.Candidate.Projects
                    .Where(static project => IsBeforeCutoff(project.Period, EarlyCareerCutoffYear))
                    .ToArray();

                // Projects whose period falls within an experience entry are
                // potentially client engagements (freelance/consulting). Only
                // fold them into the experience section when a single role
                // covers 3+ projects — indicating an umbrella consulting/freelance role.
                var umbrellaRoles = modernExperience
                    .Where(exp => modernProjects.Count(p => PeriodContains(exp.Period, p.Period)) >= 3)
                    .ToArray();
                var coveredProjects = modernProjects
                    .Where(project => umbrellaRoles.Any(exp => PeriodContains(exp.Period, project.Period)))
                    .ToArray();
                var standaloneProjects = modernProjects
                    .Except(coveredProjects)
                    .ToArray();

                AppendProfileOverview(builder, generatedBody, request, selectedEvidence, outputLanguage);
                // FitSnapshot is intentionally NOT emitted in the CV path: it is
                // an internal assessment artifact (strengths/gaps for the user's
                // own review) and must not appear in the document sent to recruiters.
                AppendExperienceList(builder, modernExperience, coveredProjects, outputLanguage, FindSection(request.GeneratedSections, CvSection.ExperienceHighlights));
                if (standaloneProjects.Length > 0)
                {
                    AppendProjects(builder, standaloneProjects, outputLanguage, FindSection(request.GeneratedSections, CvSection.ProjectHighlights));
                }

                AppendEducation(builder, request.Candidate.Education, outputLanguage);
                if (HasSelectedCertifications(selectedEvidence))
                {
                    AppendSelectedCertifications(builder, selectedEvidence, outputLanguage);
                }

                AppendLanguages(builder, request.Candidate, outputLanguage);
                AppendTopRecommendations(builder, request.Candidate, selectedEvidence, outputLanguage);
                AppendEarlyCareer(builder, earlyCareerExperience, earlyCareerProjects, outputLanguage);

                break;
            case DocumentKind.CoverLetter:
                AppendSection(builder, Translate(outputLanguage, "Letter", "Ansøgning"), generatedBody);
                AppendFitSnapshot(builder, request.JobFitAssessment, outputLanguage);
                AppendApplicantAngle(builder, request.ApplicantDifferentiatorProfile, outputLanguage);
                AppendSelectedEvidence(builder, selectedEvidence, outputLanguage);
                break;
            case DocumentKind.ProfileSummary:
                AppendSection(builder, Translate(outputLanguage, "Summary", "Profil"), generatedBody);
                AppendApplicantAngle(builder, request.ApplicantDifferentiatorProfile, outputLanguage);
                if (HasSelectedCertifications(selectedEvidence))
                {
                    AppendSelectedCertifications(builder, selectedEvidence, outputLanguage);
                }

                break;
            case DocumentKind.InterviewNotes:
                AppendSection(builder, Translate(outputLanguage, "Talking Points", "Samtalepunkter"), generatedBody);
                AppendFitSnapshot(builder, request.JobFitAssessment, outputLanguage);
                AppendSelectedEvidence(builder, selectedEvidence, outputLanguage);
                if (!selectedEvidence.Any(static item => item.Evidence.Type == CandidateEvidenceType.Recommendation))
                {
                    AppendRecommendations(builder, request, outputLanguage);
                }

                break;
        }

        return Task.FromResult(new GeneratedDocument(
            request.Kind,
            $"{request.Candidate.Name.FullName} - {request.Kind}",
            builder.ToString().Trim(),
            builder.ToString().Trim(),
            DateTimeOffset.UtcNow,
            AtsSnapshot: request.Kind is DocumentKind.Cv ? BuildAtsSnapshot(request, selectedEvidence) : null));
    }

    /// <summary>
    /// Builds a public-safe candidate snapshot the export pipeline writes into
    /// a custom XML part of the produced <c>.docx</c>. Contains structured
    /// fields ATS / LLM-based parsers can consume directly without re-parsing
    /// the rendered markdown. Excludes any internal assessment data.
    /// </summary>
    private static AtsCandidateSnapshot BuildAtsSnapshot(
        DocumentRenderRequest request,
        IReadOnlyList<RankedEvidenceItem> selectedEvidence)
    {
        var candidate = request.Candidate;
        var contact = MergeContact(request.PersonalContact, candidate);

        var skillNames = candidate.Skills.Select(s => s.Name)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var experience = candidate.Experience
            .Where(static role => !IsBeforeCutoff(role.Period, EarlyCareerCutoffYear))
            .Select(role => new AtsExperienceEntry(
                role.Title,
                role.CompanyName,
                string.IsNullOrWhiteSpace(role.Period.DisplayValue) ? null : role.Period.DisplayValue))
            .ToArray();

        var education = candidate.Education
            .Select(entry => new AtsEducationEntry(
                string.IsNullOrWhiteSpace(entry.DegreeName) ? null : entry.DegreeName,
                entry.SchoolName,
                string.IsNullOrWhiteSpace(entry.Period.DisplayValue) ? null : entry.Period.DisplayValue))
            .ToArray();

        // Certifications come from the evidence ranker so we list only the
        // ones that survived selection for this job (mirrors what the
        // rendered CV body shows).
        var certifications = selectedEvidence
            .Where(static item => item.Evidence.Type == CandidateEvidenceType.Certification)
            .Select(static item => item.Evidence.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var languages = candidate.ManualSignals.TryGetValue("Languages", out var languageBlob)
            && !string.IsNullOrWhiteSpace(languageBlob)
            ? ParseLanguageSignals(languageBlob)
            : Array.Empty<LanguageProficiency>();

        return new AtsCandidateSnapshot(
            FullName: candidate.Name.FullName,
            Headline: candidate.Headline,
            Contact: contact,
            TargetRoleTitle: request.JobPosting.RoleTitle,
            TargetCompanyName: request.JobPosting.CompanyName,
            Skills: skillNames,
            MustHaveThemes: request.JobPosting.MustHaveThemes,
            Experience: experience,
            Education: education,
            Certifications: certifications,
            Languages: languages);
    }

    /// <summary>
    /// Merges the per-job <see cref="PersonalContactInfo"/> with the candidate's
    /// profile-level fields so any field the user did not enter falls back to
    /// the LinkedIn-imported value.
    /// </summary>
    private static PersonalContactInfo? MergeContact(PersonalContactInfo? perJob, CandidateProfile candidate)
    {
        var email = FirstNonBlank(perJob?.Email, candidate.PrimaryEmail);
        var phone = perJob?.Phone;
        var linkedIn = FirstNonBlank(perJob?.LinkedInUrl, candidate.PublicProfileUrl);
        var city = FirstNonBlank(perJob?.City, candidate.Location);

        if (string.IsNullOrWhiteSpace(email)
            && string.IsNullOrWhiteSpace(phone)
            && string.IsNullOrWhiteSpace(linkedIn)
            && string.IsNullOrWhiteSpace(city))
        {
            return null;
        }

        return new PersonalContactInfo(email, phone, linkedIn, city);
    }

    /// <summary>
    /// Renders a professional profile overview section with keyword-rich technology line.
    /// Uses ATS-standard section title and includes technologies from the job posting
    /// that the candidate has evidence for.
    /// </summary>
    private static void AppendProfileOverview(
        StringBuilder builder,
        string? generatedBody,
        DocumentRenderRequest request,
        IReadOnlyList<RankedEvidenceItem> selectedEvidence,
        OutputLanguage outputLanguage)
    {
        builder.AppendLine($"## {Translate(outputLanguage, "Professional Profile", "Professionel profil")}");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(generatedBody))
        {
            builder.AppendLine(generatedBody);
            builder.AppendLine();
        }

        var keySkillsOverride = FindSection(request.GeneratedSections, CvSection.KeySkills);
        var technologies = !string.IsNullOrWhiteSpace(keySkillsOverride)
            ? keySkillsOverride!.Trim()
            : BuildKeywordLine(request, selectedEvidence);
        if (!string.IsNullOrWhiteSpace(technologies))
        {
            builder.AppendLine($"**{Translate(outputLanguage, "Key Technologies & Competencies", "Nøgleteknologier og kompetencer")}:** {technologies}");
            builder.AppendLine();
        }
    }

    private static string? FindSection(IReadOnlyList<CvSectionContent>? sections, CvSection section)
        => sections?.FirstOrDefault(s => s.Section == section)?.Markdown;

    /// <summary>
    /// Builds a comma-separated keyword line from job themes and detected technologies,
    /// filtered to only those the candidate has evidence for.
    /// </summary>
    internal static string BuildKeywordLine(DocumentRenderRequest request, IReadOnlyList<RankedEvidenceItem> selectedEvidence)
    {
        var allJobTerms = new List<string>();
        allJobTerms.AddRange(request.JobPosting.MustHaveThemes);
        allJobTerms.AddRange(request.JobPosting.NiceToHaveThemes);

        var techAssessment = request.TechnologyGapAssessment;
        if (techAssessment is not null && techAssessment.HasSignals)
        {
            allJobTerms.AddRange(techAssessment.DetectedTechnologies);
        }

        if (allJobTerms.Count == 0)
        {
            return string.Empty;
        }

        var evidenceTags = selectedEvidence
            .SelectMany(static item => item.Evidence.Tags)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matched = allJobTerms
            .Where(term => evidenceTags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase)
                || term.Contains(tag, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return string.Join(", ", matched);
    }

    /// <summary>
    /// Renders all experience entries with ATS-standard section title and clear Title/Company/Date pattern.
    /// When <paramref name="coveredProjects"/> are provided, they are rendered as key client
    /// engagements under the experience entry whose period contains them.
    /// </summary>
    private static void AppendExperienceList(
        StringBuilder builder,
        IReadOnlyList<ExperienceEntry> experience,
        IReadOnlyList<ProjectEntry> coveredProjects,
        OutputLanguage outputLanguage,
        string? sectionOverride = null)
    {
        if (experience.Count == 0)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Professional Experience", "Erhvervserfaring")}");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(sectionOverride))
        {
            builder.AppendLine(sectionOverride.Trim());
            builder.AppendLine();
            return;
        }

        foreach (var role in experience.Take(8))
        {
            var periodSuffix = string.IsNullOrWhiteSpace(role.Period.DisplayValue)
                ? string.Empty
                : $" | {role.Period.DisplayValue}";
            builder.AppendLine($"### {role.Title} | {role.CompanyName}{periodSuffix}");

            if (!string.IsNullOrWhiteSpace(role.Description))
            {
                builder.AppendLine();
                builder.AppendLine(role.Description.Trim());
            }

            // Append any client engagements (projects) whose period falls
            // within this experience entry with bold "Client:" prefix.
            var roleProjects = coveredProjects
                .Where(project => PeriodContains(role.Period, project.Period))
                .ToArray();
            if (roleProjects.Length > 0)
            {
                builder.AppendLine();
                foreach (var project in roleProjects)
                {
                    var desc = string.IsNullOrWhiteSpace(project.Description)
                        ? string.Empty
                        : $" — {project.Description.Trim()}";
                    builder.AppendLine($"- **Client: {project.Title}**{desc} ({project.Period.DisplayValue})");
                }
            }

            builder.AppendLine();
        }
    }

    /// <summary>
    /// Renders candidate projects with title, period, description, and URL.
    /// </summary>
    private static void AppendProjects(StringBuilder builder, IReadOnlyList<ProjectEntry> projects, OutputLanguage outputLanguage, string? sectionOverride = null)
    {
        if (projects.Count == 0)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Projects", "Projekter")}");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(sectionOverride))
        {
            builder.AppendLine(sectionOverride.Trim());
            builder.AppendLine();
            return;
        }

        foreach (var project in projects)
        {
            var periodSuffix = string.IsNullOrWhiteSpace(project.Period.DisplayValue)
                ? string.Empty
                : $" ({project.Period.DisplayValue})";
            var desc = string.IsNullOrWhiteSpace(project.Description)
                ? string.Empty
                : $" — {project.Description.Trim()}";
            var urlSuffix = project.Url is not null
                ? $" [{project.Url}]({project.Url})"
                : string.Empty;
            builder.AppendLine($"- **{project.Title}**{periodSuffix}{desc}{urlSuffix}");
        }

        builder.AppendLine();
    }

    private static void AppendEarlyCareer(
        StringBuilder builder,
        IReadOnlyList<ExperienceEntry> earlyCareerExperience,
        IReadOnlyList<ProjectEntry> earlyCareerProjects,
        OutputLanguage outputLanguage)
    {
        if (earlyCareerExperience.Count == 0 && earlyCareerProjects.Count == 0)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Early Career", "Tidlig karriere")}");
        builder.AppendLine();

        foreach (var role in earlyCareerExperience)
        {
            var periodSuffix = string.IsNullOrWhiteSpace(role.Period.DisplayValue)
                ? string.Empty
                : $" ({role.Period.DisplayValue})";
            builder.AppendLine($"- **{role.Title}** | {role.CompanyName}{periodSuffix}");
        }

        foreach (var project in earlyCareerProjects)
        {
            var periodSuffix = string.IsNullOrWhiteSpace(project.Period.DisplayValue)
                ? string.Empty
                : $" ({project.Period.DisplayValue})";
            builder.AppendLine($"- **{project.Title}**{periodSuffix}");
        }

        builder.AppendLine();
    }

    /// <summary>
    /// Renders the top 3 recommendations ranked by evidence score for the
    /// current job context, each with a heading and blockquote.
    /// </summary>
    private static void AppendTopRecommendations(
        StringBuilder builder,
        CandidateProfile candidate,
        IReadOnlyList<RankedEvidenceItem> selectedEvidence,
        OutputLanguage outputLanguage)
    {
        if (candidate.Recommendations.Count == 0)
        {
            return;
        }

        // Build a lookup of evidence scores for recommendation authors.
        var recommendationScores = selectedEvidence
            .Where(static item => item.Evidence.Type is CandidateEvidenceType.Recommendation)
            .ToDictionary(
                static item => item.Evidence.Title,
                static item => item.Score,
                StringComparer.OrdinalIgnoreCase);

        // Rank: evidence-backed first (by score), then original order. Cap at 3.
        var ranked = candidate.Recommendations
            .OrderByDescending(rec =>
                recommendationScores.TryGetValue($"Recommendation from {rec.Author.FullName}", out var score)
                    ? score
                    : -1)
            .Take(3)
            .ToArray();

        builder.AppendLine($"## {Translate(outputLanguage, "Recommendations", "Anbefalinger")}");
        builder.AppendLine();

        foreach (var recommendation in ranked)
        {
            var authorLine = $"{recommendation.Author.FullName}{FormatAt(recommendation.Company, outputLanguage)}";
            if (!string.IsNullOrWhiteSpace(recommendation.JobTitle))
            {
                authorLine += $", {recommendation.JobTitle}";
            }

            var translationNote = GetTranslationAnnotation(recommendation.Text, outputLanguage);
            builder.AppendLine($"> *\"{recommendation.Text}\"{translationNote}*");
            builder.AppendLine($"> — {authorLine}");
            builder.AppendLine();
        }
    }

    /// <summary>
    /// Detects the language of a text and returns a translation annotation if it does not
    /// match the target output language. Uses a simple Danish word-frequency heuristic.
    /// </summary>
    internal static string GetTranslationAnnotation(string text, OutputLanguage outputLanguage)
    {
        var isDanish = DetectDanish(text);

        return (isDanish, outputLanguage) switch
        {
            (true, OutputLanguage.English) => " (translated from Danish)",
            (false, OutputLanguage.Danish) => " (translated from English)",
            _ => string.Empty
        };
    }

    /// <summary>
    /// Returns true when the text is likely Danish, based on the density of common Danish words.
    /// A threshold of 8% of total words being Danish markers indicates Danish text.
    /// </summary>
    internal static bool DetectDanish(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var words = text.Split([' ', '\t', '\n', '\r', ',', '.', '!', '?', ';', ':', '"', '\'', '(', ')'],
            StringSplitOptions.RemoveEmptyEntries);

        if (words.Length < 5)
        {
            return false;
        }

        var danishCount = words.Count(word => DanishMarkers.Contains(word));
        var ratio = (double)danishCount / words.Length;

        return ratio >= 0.08;
    }

    /// <summary>
    /// A role or project is "early career" when it was completed before the cutoff year.
    /// Ongoing items (no end date) are never early career. Falls back to start year
    /// only when no end date is available.
    /// </summary>
    private static bool IsBeforeCutoff(DateRange period, int cutoffYear)
    {
        var endYear = period.FinishedOn?.Year;
        if (endYear is not null)
        {
            return endYear.Value < cutoffYear;
        }

        // No end date: treat ongoing/current items as modern.
        return false;
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

    private static void AppendSection(StringBuilder builder, string title, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine(content);
        builder.AppendLine();
    }

    private static void AppendFitSnapshot(StringBuilder builder, JobFitAssessment? assessment, OutputLanguage outputLanguage)
    {
        if (assessment is null || !assessment.HasSignals || assessment.Strengths.Count == 0)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Fit Snapshot", "Matchvurdering")}");
        builder.AppendLine();

        foreach (var strength in assessment.Strengths.Take(3))
        {
            builder.AppendLine($"- {Translate(outputLanguage, "Strength", "Styrke")}: {strength}");
        }

        builder.AppendLine();
    }

    private static void AppendApplicantAngle(StringBuilder builder, ApplicantDifferentiatorProfile? differentiatorProfile, OutputLanguage outputLanguage)
    {
        if (differentiatorProfile is null || !differentiatorProfile.HasContent)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Applicant Angle", "Kandidatvinkel")}");
        builder.AppendLine();
        foreach (var line in differentiatorProfile.ToSummaryLines())
        {
            builder.AppendLine($"- {line}");
        }

        builder.AppendLine();
    }

    private static void AppendSelectedEvidence(StringBuilder builder, IReadOnlyList<RankedEvidenceItem> selectedEvidence, OutputLanguage outputLanguage)
    {
        var proofItems = selectedEvidence
            .Take(6)
            .ToArray();

        if (proofItems.Length == 0)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Selected Proof Points", "Udvalgt dokumentation")}");
        builder.AppendLine();
        foreach (var item in proofItems)
        {
            builder.AppendLine($"- {item.Evidence.Title}: {item.Evidence.Summary}");
        }

        builder.AppendLine();
    }

    private static void AppendSelectedCertifications(StringBuilder builder, IReadOnlyList<RankedEvidenceItem> selectedEvidence, OutputLanguage outputLanguage)
    {
        var certItems = selectedEvidence
            .Where(static item => item.Evidence.Type is CandidateEvidenceType.Certification)
            .Take(8)
            .ToArray();

        if (certItems.Length == 0)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Certifications", "Certificeringer")}");
        builder.AppendLine();
        foreach (var item in certItems)
        {
            builder.AppendLine($"- {item.Evidence.Title}");
        }

        builder.AppendLine();
    }

    private static void AppendRecommendations(StringBuilder builder, DocumentRenderRequest request, OutputLanguage outputLanguage)
    {
        if (request.Candidate.Recommendations.Count == 0)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Recommendations", "Anbefalinger")}");
        builder.AppendLine();
        foreach (var recommendation in request.Candidate.Recommendations.Take(3))
        {
            builder.AppendLine($"- {recommendation.Author.FullName}{FormatAt(recommendation.Company, outputLanguage)}: {recommendation.Text}");
        }

        builder.AppendLine();
    }

    private static string FormatAt(string? company, OutputLanguage outputLanguage)
        => string.IsNullOrWhiteSpace(company)
            ? string.Empty
            : $" {Translate(outputLanguage, "at", "hos")} {company}";

    private static bool HasSelectedCertifications(IReadOnlyList<RankedEvidenceItem> selectedEvidence)
        => selectedEvidence.Any(static item => item.Evidence.Type is CandidateEvidenceType.Certification);

    private static string Translate(OutputLanguage outputLanguage, string english, string danish)
        => outputLanguage == OutputLanguage.Danish ? danish : english;

    /// <summary>
    /// Emits a single-line contact paragraph (email · phone · LinkedIn · city)
    /// directly under the headline blockquote when <paramref name="contact"/>
    /// has any value. Falls back to the candidate's profile fields when the
    /// per-job contact does not supply them, so the header is never empty for
    /// candidates whose LinkedIn export already carries an email or location.
    /// </summary>
    private static void AppendContactLine(StringBuilder builder, PersonalContactInfo? contact, CandidateProfile candidate)
    {
        var email = FirstNonBlank(contact?.Email, candidate.PrimaryEmail);
        var phone = contact?.Phone;
        var linkedIn = FirstNonBlank(contact?.LinkedInUrl, candidate.PublicProfileUrl);
        var city = FirstNonBlank(contact?.City, candidate.Location);

        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(email)) parts.Add(email!.Trim());
        if (!string.IsNullOrWhiteSpace(phone)) parts.Add(phone!.Trim());
        if (!string.IsNullOrWhiteSpace(linkedIn)) parts.Add(linkedIn!.Trim());
        if (!string.IsNullOrWhiteSpace(city)) parts.Add(city!.Trim());

        if (parts.Count == 0)
        {
            return;
        }

        builder.AppendLine(string.Join(" · ", parts));
        builder.AppendLine();
    }

    private static string? FirstNonBlank(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Renders the Education section from the typed
    /// <see cref="CandidateProfile.Education"/> list. Each entry is one bullet
    /// in the form <c>**Degree** | School (period)</c>; missing fields are
    /// quietly omitted so partial data still renders cleanly.
    /// </summary>
    private static void AppendEducation(StringBuilder builder, IReadOnlyList<EducationEntry> education, OutputLanguage outputLanguage)
    {
        if (education.Count == 0)
        {
            return;
        }

        builder.AppendLine($"## {CvSectionLabels.Heading(CvSection.Education, outputLanguage)}");
        builder.AppendLine();

        foreach (var entry in education)
        {
            var degreePart = string.IsNullOrWhiteSpace(entry.DegreeName)
                ? entry.SchoolName
                : $"**{entry.DegreeName.Trim()}** | {entry.SchoolName}";
            var periodSuffix = string.IsNullOrWhiteSpace(entry.Period.DisplayValue)
                ? string.Empty
                : $" ({entry.Period.DisplayValue})";
            builder.AppendLine($"- {degreePart}{periodSuffix}");
        }

        builder.AppendLine();
    }

    /// <summary>
    /// Renders the Languages section by parsing
    /// <c>CandidateProfile.ManualSignals["Languages"]</c>. The LinkedIn
    /// importer stores one language per line in the form
    /// <c>Language Name — Proficiency: Level</c>; this method preserves the
    /// language tokens, simplifies the proficiency hint, and emits a single
    /// comma-separated paragraph ("Danish — Native, English — Professional")
    /// suitable for ATS keyword scans.
    /// </summary>
    private static void AppendLanguages(StringBuilder builder, CandidateProfile candidate, OutputLanguage outputLanguage)
    {
        if (!candidate.ManualSignals.TryGetValue("Languages", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var entries = ParseLanguageSignals(raw);
        if (entries.Count == 0)
        {
            return;
        }

        builder.AppendLine($"## {CvSectionLabels.Heading(CvSection.Languages, outputLanguage)}");
        builder.AppendLine();
        builder.AppendLine(string.Join(", ", entries.Select(e =>
            string.IsNullOrWhiteSpace(e.Level)
                ? e.Language
                : $"{e.Language} — {e.Level}")));
        builder.AppendLine();
    }

    /// <summary>
    /// Parses the multi-line <c>ManualSignals["Languages"]</c> blob into
    /// <see cref="LanguageProficiency"/> records. Each input line contains
    /// the language followed by an optional <c>Proficiency: ...</c> tail.
    /// </summary>
    internal static IReadOnlyList<LanguageProficiency> ParseLanguageSignals(string raw)
    {
        var lines = raw.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        var result = new List<LanguageProficiency>(lines.Length);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            string language;
            string? level = null;

            // Common shapes from the LinkedIn importer:
            //   "English — Proficiency: Native or bilingual"
            //   "Danish - Proficiency: Native or bilingual"
            //   "German — Professional working"
            //   "French"
            var separatorIndex = line.IndexOfAny(['—', '-', '–']);
            if (separatorIndex > 0)
            {
                language = line[..separatorIndex].Trim();
                var tail = line[(separatorIndex + 1)..].Trim();
                const string proficiencyPrefix = "Proficiency:";
                if (tail.StartsWith(proficiencyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    tail = tail[proficiencyPrefix.Length..].Trim();
                }
                level = string.IsNullOrWhiteSpace(tail) ? null : tail;
            }
            else
            {
                language = line;
            }

            if (!string.IsNullOrWhiteSpace(language))
            {
                result.Add(new LanguageProficiency(language, level));
            }
        }

        return result;
    }
}

