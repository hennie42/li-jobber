using System.Text;
using System.Text.RegularExpressions;

namespace LiCvWriter.Infrastructure.Workflows;

/// <summary>
/// Defensive normalization for markdown produced by local LLMs. Local Ollama
/// models frequently emit malformed bullet lists — bullets glued to the
/// preceding sentence (<c>"finished.*Next item"</c>), missing blank lines
/// before headings/lists, or runs of inline <c>"* a* b* c"</c> that Markdig
/// would otherwise interpret as emphasis. This class rewrites those patterns
/// into clean GitHub-flavored markdown so downstream conversion to Word
/// produces real headings and bullet lists.
/// </summary>
internal static class LlmMarkdownNormalizer
{
    // Matches '*' or '•' glued to the end of a sentence, where the next char is
    // an uppercase letter (indicating a new sentence / bullet). By requiring
    // [A-Z] after the marker we avoid breaking emphasis (*italic*, **bold**).
    // Punctuation-lead variant: `. *Next` or `.*Next` or `.•Next`.
    private static readonly Regex InlineBulletAfterPunctuation = new(
        @"(?<lead>[.!?:;,)\]""'])[ \t]*[\*•][ \t]*(?=[A-Z])",
        RegexOptions.Compiled);

    // Non-punctuation-lead variant: any word char directly followed by '*' or
    // '•' then an uppercase letter. Catches `Present*Architected`.
    private static readonly Regex InlineBulletAfterWord = new(
        @"(?<lead>\w)[\*•][ \t]*(?=[A-Z])",
        RegexOptions.Compiled);

    // Matches a lone '-' bullet that starts right after text on the same line
    // (e.g. "Oct2025 - Present- Architected"). The negative lookbehind avoids
    // matching "2020 - 2024" date ranges: we require a non-space before the '-'.
    private static readonly Regex InlineDashBullet = new(
        @"(?<lead>[.!?)\]""'])[ \t]*-[ \t]+(?=[A-Z])",
        RegexOptions.Compiled);

    // Matches a heading marker glued to the end of a sentence (e.g. "text.### Heading").
    // Requires whitespace OR sentence-ending punctuation between the lead char
    // and the hashes so we never split language tokens like "C#", "F#" or
    // "C# 13" that legitimately contain a '#' adjacent to a letter/digit.
    private static readonly Regex InlineHeading = new(
        @"(?<lead>[.!?:;,)\]""'])\s*(?<hashes>#{1,6})\s+(?=\S)",
        RegexOptions.Compiled);

    // A date pattern glued to the preceding word, e.g. "NordiskOct2025" or
    // "ConsultingAug2020". We insert a newline to separate the date from the
    // company/title heading.
    private static readonly Regex GluedDate = new(
        @"(?<lead>[a-z])(?<date>(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec|January|February|March|April|May|June|July|August|September|October|November|December)\s*\d{4})",
        RegexOptions.Compiled);

    // Detects a "Title | Company" role header appearing inline after
    // sentence-ending punctuation. Common when local models merge consecutive
    // role entries into a single paragraph without ### markers. Requires at
    // least 4 characters on each side of the pipe and uppercase after the pipe
    // to avoid false positives.
    private static readonly Regex InlineRoleHeader = new(
        @"(?<lead>[.!?)\]""'])\s+(?<heading>[A-Z][^|\n]{3,}\|\s*[A-Z][^\n.!?]{3,})",
        RegexOptions.Compiled);

    // Trailing period on a heading line.
    private static readonly Regex HeadingTrailingPeriod = new(
        @"^(?<heading>#{1,6}\s.+?)\.\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Three or more consecutive blank lines.
    private static readonly Regex ExcessBlankLines = new(
        @"(\r?\n[ \t]*){3,}",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns a normalized copy of <paramref name="markdown"/> with bullets,
    /// headings, and blank-line spacing repaired. Returns the input unchanged
    /// when it is null, empty, or already well-formed.
    /// </summary>
    public static string Normalize(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown ?? string.Empty;
        }

        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');

        // 1. Separate dates glued to company names (e.g. "NordiskOct2025").
        text = GluedDate.Replace(text, "${lead}\n${date}");

        // 2. Split inline-glued bullets into their own lines.
        //    Run punctuation-lead first (highest confidence), then word-lead.
        text = InlineBulletAfterPunctuation.Replace(text, m =>
            $"{m.Groups["lead"].Value}\n- ");
        text = InlineBulletAfterWord.Replace(text, m =>
            $"{m.Groups["lead"].Value}\n- ");
        text = InlineDashBullet.Replace(text, m =>
            $"{m.Groups["lead"].Value}\n- ");

        // 2b. Promote inline "Title | Company" role headers into ### headings
        //     so Markdig produces separate h3 elements for each role.
        text = InlineRoleHeader.Replace(text, m =>
            $"{m.Groups["lead"].Value}\n\n### {m.Groups["heading"].Value}");

        // 3. Split inline-glued headings into their own lines.
        text = InlineHeading.Replace(text, m =>
            $"{m.Groups["lead"].Value}\n\n{m.Groups["hashes"].Value} ");

        // 4. Per-line cleanup: normalize bullet markers, ensure space after
        //    them, and strip trailing whitespace.
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            line = NormalizeBulletLine(line);
            lines[i] = line;
        }
        text = string.Join('\n', lines);

        // 5. Ensure a blank line precedes any heading or list line so Markdig
        //    treats them as block-level constructs.
        text = EnsureBlankLineBeforeBlock(text);

        // 6. Drop trailing periods on heading lines (e.g. "### Title | Co.").
        text = HeadingTrailingPeriod.Replace(text, "${heading}");

        // 7. Collapse runs of 3+ blank lines into 2.
        text = ExcessBlankLines.Replace(text, "\n\n");

        return text.Trim() + "\n";
    }

    private static string NormalizeBulletLine(string line)
    {
        // Match a leading bullet marker (*, -, •) on its own line. Standardize
        // to "- " and ensure exactly one space after the marker.
        var match = Regex.Match(line, @"^(?<indent>\s*)(?<marker>[\*\-•])(?<rest>.*)$");
        if (!match.Success)
        {
            return line;
        }

        var rest = match.Groups["rest"].Value;
        if (rest.Length == 0)
        {
            return line;
        }

        // Skip horizontal rules ("---", "***") and emphasis runs ("**bold**").
        if (rest.StartsWith('*') || rest.StartsWith('-'))
        {
            return line;
        }

        var indent = match.Groups["indent"].Value;
        var content = rest.TrimStart();
        if (content.Length == 0)
        {
            return line;
        }

        return $"{indent}- {content}";
    }

    private static string EnsureBlankLineBeforeBlock(string text)
    {
        var lines = text.Split('\n');
        var builder = new StringBuilder(text.Length + 64);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var isBlock = IsHeadingLine(line) || IsBulletLine(line);

            if (isBlock && i > 0)
            {
                var previous = lines[i - 1];
                if (previous.Length > 0 && !IsBulletLine(previous) && !IsHeadingLine(previous))
                {
                    builder.Append('\n');
                }
            }

            builder.Append(line);
            if (i < lines.Length - 1)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    private static bool IsHeadingLine(string line)
        => Regex.IsMatch(line, @"^\s*#{1,6}\s+\S");

    private static bool IsBulletLine(string line)
        => Regex.IsMatch(line, @"^\s*-\s+\S");
}
