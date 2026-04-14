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

        builder.AppendLine($"## {Translate(outputLanguage, "Target Role", "Målrolle")}");
        builder.AppendLine();
        builder.AppendLine($"- {Translate(outputLanguage, "Role", "Rolle")}: {request.JobPosting.RoleTitle}");
        builder.AppendLine($"- {Translate(outputLanguage, "Company", "Virksomhed")}: {request.JobPosting.CompanyName}");
        builder.AppendLine();

        var generatedBody = !string.IsNullOrWhiteSpace(request.GeneratedBody)
            ? request.GeneratedBody.Trim()
            : request.Candidate.Summary?.Trim();

        switch (request.Kind)
        {
            case DocumentKind.Cv:
                AppendProfileOverview(builder, generatedBody, request, selectedEvidence, outputLanguage);
                AppendFitSnapshot(builder, request.JobFitAssessment, outputLanguage);
                AppendExperienceList(builder, request.Candidate, outputLanguage);
                AppendProjects(builder, request.Candidate, outputLanguage);
                AppendAllRecommendations(builder, request.Candidate, outputLanguage);
                if (HasSelectedCertifications(selectedEvidence))
                {
                    AppendSelectedCertifications(builder, selectedEvidence, outputLanguage);
                }

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
            DateTimeOffset.UtcNow));
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

        var technologies = BuildKeywordLine(request, selectedEvidence);
        if (!string.IsNullOrWhiteSpace(technologies))
        {
            builder.AppendLine($"**{Translate(outputLanguage, "Key Technologies & Competencies", "Nøgleteknologier og kompetencer")}:** {technologies}");
            builder.AppendLine();
        }
    }

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
    /// </summary>
    private static void AppendExperienceList(StringBuilder builder, CandidateProfile candidate, OutputLanguage outputLanguage)
    {
        if (candidate.Experience.Count == 0)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Professional Experience", "Erhvervserfaring")}");
        builder.AppendLine();

        foreach (var role in candidate.Experience.Take(12))
        {
            builder.AppendLine($"### {role.Title} | {role.CompanyName}");
            if (!string.IsNullOrWhiteSpace(role.Period.DisplayValue))
            {
                builder.AppendLine(role.Period.DisplayValue);
            }

            if (!string.IsNullOrWhiteSpace(role.Description))
            {
                builder.AppendLine();
                builder.AppendLine(role.Description.Trim());
            }

            builder.AppendLine();
        }
    }

    /// <summary>
    /// Renders candidate projects with title, period, description, and URL.
    /// </summary>
    private static void AppendProjects(StringBuilder builder, CandidateProfile candidate, OutputLanguage outputLanguage)
    {
        if (candidate.Projects.Count == 0)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Projects", "Projekter")}");
        builder.AppendLine();

        foreach (var project in candidate.Projects)
        {
            builder.AppendLine($"### {project.Title}");
            if (!string.IsNullOrWhiteSpace(project.Period.DisplayValue))
            {
                builder.AppendLine(project.Period.DisplayValue);
            }

            if (!string.IsNullOrWhiteSpace(project.Description))
            {
                builder.AppendLine();
                builder.AppendLine(project.Description.Trim());
            }

            if (project.Url is not null)
            {
                builder.AppendLine();
                builder.AppendLine($"[{project.Url}]({project.Url})");
            }

            builder.AppendLine();
        }
    }

    /// <summary>
    /// Renders all recommendations with language detection and translation annotation.
    /// If a recommendation is in a different language than the output, it is annotated
    /// with "(translated from &lt;original language&gt;)".
    /// </summary>
    private static void AppendAllRecommendations(StringBuilder builder, CandidateProfile candidate, OutputLanguage outputLanguage)
    {
        if (candidate.Recommendations.Count == 0)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Recommendations", "Anbefalinger")}");
        builder.AppendLine();

        foreach (var recommendation in candidate.Recommendations)
        {
            var authorLabel = $"**{recommendation.Author.FullName}**{FormatAt(recommendation.Company, outputLanguage)}";
            if (!string.IsNullOrWhiteSpace(recommendation.JobTitle))
            {
                authorLabel += $", {recommendation.JobTitle}";
            }

            builder.AppendLine(authorLabel);
            builder.AppendLine();

            var translationNote = GetTranslationAnnotation(recommendation.Text, outputLanguage);
            builder.AppendLine($"> {recommendation.Text}{translationNote}");
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
            (true, OutputLanguage.English) => " *(translated from Danish)*",
            (false, OutputLanguage.Danish) => " *(translated from English)*",
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
}

