using System.Text;

namespace LiCvWriter.Infrastructure.Documents;

/// <summary>
/// Splits the rendered CV markdown produced by <see cref="MarkdownDocumentRenderer"/>
/// into per-section fragments that can be inserted into the template's content
/// controls. Section headings are matched against both English and Danish
/// labels because the renderer emits localized headings based on
/// <see cref="LiCvWriter.Application.Models.OutputLanguage"/>.
/// </summary>
internal static class CvMarkdownSectionExtractor
{
    /// <summary>
    /// Marker used by <c>MarkdownDocumentRenderer.AppendProfileOverview</c> to
    /// introduce the keyword line. The English / Danish variants are stored as
    /// prefixes so we can split the profile body from the keyword line.
    /// </summary>
    private static readonly string[] KeySkillsLinePrefixes =
    [
        "**Key Technologies & Competencies",
        "**Nøgleteknologier og kompetencer"
    ];

    /// <summary>
    /// Returns the candidate header section: the leading H1 name, the optional
    /// blockquote headline, and the Target Role / Målrolle bullet block.
    /// Stops at the first <c>##</c> heading that is not the target role.
    /// </summary>
    public static string? ExtractCandidateHeader(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var lines = SplitLines(markdown);
        var builder = new StringBuilder();
        var passedTargetRole = false;

        foreach (var line in lines)
        {
            if (IsHeadingLine(line, out var headingText))
            {
                var isTargetRole = MatchesHeading(headingText, "Target Role", "Målrolle");
                if (!isTargetRole && passedTargetRole)
                {
                    break;
                }

                if (isTargetRole)
                {
                    passedTargetRole = true;
                }
            }

            builder.AppendLine(line);
        }

        var result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    /// <summary>
    /// Returns the body of the Professional Profile / Professionel profil
    /// section, excluding the bold "Key Technologies" line which is rendered
    /// into its own <c>KeySkills</c> control.
    /// </summary>
    public static string? ExtractProfileSummary(string markdown)
    {
        var profileBlock = ExtractSection(markdown, "Professional Profile", "Professionel profil");
        if (profileBlock is null)
        {
            return null;
        }

        var lines = SplitLines(profileBlock);
        var builder = new StringBuilder();

        foreach (var line in lines)
        {
            if (StartsWithAny(line, KeySkillsLinePrefixes))
            {
                break;
            }

            builder.AppendLine(line);
        }

        var result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    /// <summary>
    /// Returns just the bold "Key Technologies & Competencies" line from the
    /// Professional Profile section, with the bold marker stripped so it can
    /// be styled by the template.
    /// </summary>
    public static string? ExtractKeySkills(string markdown)
    {
        var profileBlock = ExtractSection(markdown, "Professional Profile", "Professionel profil");
        if (profileBlock is null)
        {
            return null;
        }

        var line = SplitLines(profileBlock)
            .FirstOrDefault(l => StartsWithAny(l, KeySkillsLinePrefixes));

        if (line is null)
        {
            return null;
        }

        // Strip the leading "**Key Technologies & Competencies:**" / Danish
        // equivalent so the resulting fragment is just the comma-separated list.
        var colonIndex = line.IndexOf(":**", StringComparison.Ordinal);
        if (colonIndex < 0)
        {
            return line.Trim();
        }

        var keywords = line[(colonIndex + ":**".Length)..].Trim();
        return string.IsNullOrWhiteSpace(keywords) ? null : keywords;
    }

    /// <summary>
    /// Extracts the body of an <c>## {heading}</c> section, returning all lines
    /// between the heading and the next <c>##</c> heading (or end of document).
    /// Returns <see langword="null"/> when no matching heading is found or the
    /// section body is empty.
    /// </summary>
    /// <param name="markdown">The rendered CV markdown.</param>
    /// <param name="englishHeading">The English section heading (without the <c>## </c> prefix).</param>
    /// <param name="danishHeading">The Danish section heading.</param>
    public static string? ExtractSection(string markdown, string englishHeading, string danishHeading)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var lines = SplitLines(markdown);
        var startIndex = -1;

        for (var index = 0; index < lines.Length; index++)
        {
            if (IsHeadingLine(lines[index], out var headingText)
                && MatchesHeading(headingText, englishHeading, danishHeading))
            {
                startIndex = index;
                break;
            }
        }

        if (startIndex < 0)
        {
            return null;
        }

        var endIndex = lines.Length;
        for (var index = startIndex + 1; index < lines.Length; index++)
        {
            if (IsHeadingLine(lines[index], out _))
            {
                endIndex = index;
                break;
            }
        }

        var builder = new StringBuilder();
        for (var index = startIndex; index < endIndex; index++)
        {
            builder.AppendLine(lines[index]);
        }

        var body = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    private static string[] SplitLines(string markdown)
        => markdown.Split(["\r\n", "\n"], StringSplitOptions.None);

    private static bool IsHeadingLine(string line, out string headingText)
    {
        if (line.StartsWith("## ", StringComparison.Ordinal))
        {
            headingText = line[3..].Trim();
            return true;
        }

        headingText = string.Empty;
        return false;
    }

    private static bool MatchesHeading(string headingText, string english, string danish)
        => headingText.Equals(english, StringComparison.OrdinalIgnoreCase)
            || headingText.Equals(danish, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithAny(string line, IReadOnlyList<string> prefixes)
    {
        var trimmed = line.TrimStart();
        for (var index = 0; index < prefixes.Count; index++)
        {
            if (trimmed.StartsWith(prefixes[index], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
