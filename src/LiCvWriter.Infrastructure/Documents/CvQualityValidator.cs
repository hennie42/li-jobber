using LiCvWriter.Application.Models;
using LiCvWriter.Core.Documents;

namespace LiCvWriter.Infrastructure.Documents;

/// <summary>
/// Applies lightweight, deterministic CV quality checks and safe auto-fixes before export.
/// </summary>
public sealed class CvQualityValidator
{
    private const int MaxProfileNonEmptyLines = 4;
    private const int MaxCvNonEmptyLines = 80;
    private static readonly string[] OptionalTrimOrder =
    [
        "Recommendations",
        "Anbefalinger",
        "Certifications",
        "Certificeringer",
        "Projects",
        "Projekter"
    ];

    public CvQualityValidationResult ValidateAndAutoFix(GeneratedDocument document, DraftGenerationRequest request)
    {
        if (document.Kind is not DocumentKind.Cv)
        {
            return CvQualityValidationResult.Unchanged(document);
        }

        var markdown = document.Markdown;

        var fixes = new List<string>();
        var processedMarkdown = TrimProfessionalProfile(markdown, out var summaryTrimmed);
        if (summaryTrimmed)
        {
            fixes.Add("TrimmedProfessionalProfile");
        }

        processedMarkdown = ReorderSectionsByKeywordCoverage(processedMarkdown, request.JobPosting.MustHaveThemes, out var sectionOrderChanged);
        if (sectionOrderChanged)
        {
            fixes.Add("ReorderedSectionsForKeywordCoverage");
        }

        processedMarkdown = TrimOptionalSectionsForLength(processedMarkdown, out var trimmedSections);
        if (trimmedSections.Count > 0)
        {
            fixes.Add("TrimmedOptionalSectionsForLength");
        }

        var quantifiedBulletCount = CountQuantifiedBullets(processedMarkdown);
        var missingThemes = request.JobPosting.MustHaveThemes
            .Where(theme => !string.IsNullOrWhiteSpace(theme)
                && !processedMarkdown.Contains(theme, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var missingMustHaveThemeCount = missingThemes.Length;
        var totalMustHave = request.JobPosting.MustHaveThemes.Count(static t => !string.IsNullOrWhiteSpace(t));
        var atsKeywordCoveragePercent = totalMustHave > 0
            ? (int)Math.Round(100.0 * (totalMustHave - missingMustHaveThemeCount) / totalMustHave)
            : 0;

        var updatedDocument = fixes.Count > 0
            ? document with { Markdown = processedMarkdown, PlainText = processedMarkdown }
            : document;

        var report = new CvQualityReport(
            missingMustHaveThemeCount,
            quantifiedBulletCount,
            summaryTrimmed,
            sectionOrderChanged,
            trimmedSections,
            fixes,
            missingThemes,
            atsKeywordCoveragePercent);

        return new CvQualityValidationResult(updatedDocument, report);
    }

    private static int CountQuantifiedBullets(string markdown)
        => markdown
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Count(static line =>
            {
                var trimmed = line.TrimStart();
                return (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                    && trimmed.Any(char.IsDigit);
            });

    private static string TrimProfessionalProfile(string markdown, out bool changed)
    {
        var lines = markdown.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();
        var sectionIndex = lines.FindIndex(static line =>
            line.Equals("## Professional Profile", StringComparison.OrdinalIgnoreCase)
            || line.Equals("## Professionel profil", StringComparison.OrdinalIgnoreCase));

        if (sectionIndex < 0)
        {
            changed = false;
            return markdown;
        }

        var nextSectionIndex = lines.FindIndex(sectionIndex + 1, static line => line.StartsWith("## ", StringComparison.Ordinal));
        if (nextSectionIndex < 0)
        {
            nextSectionIndex = lines.Count;
        }

        var profileContentStart = sectionIndex + 1;
        var nonEmptySeen = 0;
        var keepUntil = profileContentStart;

        for (var index = profileContentStart; index < nextSectionIndex; index++)
        {
            var current = lines[index];

            if (current.StartsWith("**Key Technologies & Competencies", StringComparison.Ordinal)
                || current.StartsWith("**Nøgleteknologier og kompetencer", StringComparison.Ordinal))
            {
                keepUntil = index;
                break;
            }

            if (!string.IsNullOrWhiteSpace(current))
            {
                nonEmptySeen++;
            }

            if (nonEmptySeen <= MaxProfileNonEmptyLines)
            {
                keepUntil = index + 1;
            }
        }

        if (keepUntil >= nextSectionIndex)
        {
            changed = false;
            return markdown;
        }

        lines.RemoveRange(keepUntil, nextSectionIndex - keepUntil);
        changed = true;
        return string.Join(Environment.NewLine, lines);
    }

    private static string ReorderSectionsByKeywordCoverage(
        string markdown,
        IReadOnlyList<string> mustHaveThemes,
        out bool changed)
    {
        changed = false;
        if (mustHaveThemes.Count == 0)
        {
            return markdown;
        }

        var sections = ParseSections(markdown);
        var experienceIndex = sections.FindIndex(static section => IsHeading(section.Heading, "Professional Experience", "Erhvervserfaring"));
        var projectsIndex = sections.FindIndex(static section => IsHeading(section.Heading, "Projects", "Projekter"));

        if (experienceIndex < 0 || projectsIndex < 0 || projectsIndex < experienceIndex)
        {
            return markdown;
        }

        var experienceHits = CountThemeHits(sections[experienceIndex].Body, mustHaveThemes);
        var projectHits = CountThemeHits(sections[projectsIndex].Body, mustHaveThemes);

        if (projectHits <= experienceHits)
        {
            return markdown;
        }

        var projectsSection = sections[projectsIndex];
        sections.RemoveAt(projectsIndex);
        sections.Insert(experienceIndex, projectsSection);
        changed = true;
        return ComposeMarkdown(sections);
    }

    private static string TrimOptionalSectionsForLength(string markdown, out IReadOnlyList<string> trimmedSectionNames)
    {
        var sections = ParseSections(markdown);
        var trimmed = new List<string>();

        foreach (var heading in OptionalTrimOrder)
        {
            if (CountNonEmptyLines(ComposeMarkdown(sections)) <= MaxCvNonEmptyLines)
            {
                break;
            }

            var index = sections.FindIndex(section => section.Heading.Equals(heading, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                continue;
            }

            trimmed.Add(sections[index].Heading);
            sections.RemoveAt(index);
        }

        trimmedSectionNames = trimmed;
        return trimmed.Count == 0 ? markdown : ComposeMarkdown(sections);
    }

    private static int CountThemeHits(string sectionBody, IReadOnlyList<string> themes)
        => themes
            .Where(theme => !string.IsNullOrWhiteSpace(theme))
            .Select(theme => theme.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(theme => sectionBody.Contains(theme, StringComparison.OrdinalIgnoreCase));

    private static int CountNonEmptyLines(string markdown)
        => markdown
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Count(static line => !string.IsNullOrWhiteSpace(line));

    private static List<MarkdownSection> ParseSections(string markdown)
    {
        var lines = markdown.Split(["\r\n", "\n"], StringSplitOptions.None);
        var sections = new List<MarkdownSection>();
        var currentHeading = string.Empty;
        var currentLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (currentLines.Count > 0 || !string.IsNullOrWhiteSpace(currentHeading))
                {
                    sections.Add(new MarkdownSection(currentHeading, currentLines.ToArray()));
                }

                currentHeading = line[3..].Trim();
                currentLines = [line];
                continue;
            }

            currentLines.Add(line);
        }

        if (currentLines.Count > 0 || !string.IsNullOrWhiteSpace(currentHeading))
        {
            sections.Add(new MarkdownSection(currentHeading, currentLines.ToArray()));
        }

        return sections;
    }

    private static string ComposeMarkdown(IReadOnlyList<MarkdownSection> sections)
        => string.Join(Environment.NewLine, sections.SelectMany(static section => section.Lines));

    private static bool IsHeading(string heading, string english, string danish)
        => heading.Equals(english, StringComparison.OrdinalIgnoreCase)
           || heading.Equals(danish, StringComparison.OrdinalIgnoreCase);

    private sealed record MarkdownSection(string Heading, IReadOnlyList<string> Lines)
    {
        public string Body => string.Join(Environment.NewLine, Lines.Skip(1));
    }
}
