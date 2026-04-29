using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LiCvWriter.Web.SharedUI.Markdown;

/// <summary>
/// Converts a safe subset of generated Markdown into HTML for browser-side previews.
/// </summary>
public static partial class ClientMarkdownRenderer
{
    /// <summary>
    /// Renders Markdown into encoded HTML for display inside the local preview surface.
    /// </summary>
    public static string RenderHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "<p class=\"markdown-preview-empty\">No preview content yet.</p>";
        }

        var builder = new StringBuilder(markdown.Length + 256);
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var listOpen = false;
        var codeOpen = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (codeOpen)
                {
                    builder.AppendLine("</code></pre>");
                    codeOpen = false;
                }
                else
                {
                    CloseList(builder, ref listOpen);
                    builder.AppendLine("<pre><code>");
                    codeOpen = true;
                }

                continue;
            }

            if (codeOpen)
            {
                builder.Append(WebUtility.HtmlEncode(line));
                builder.Append('\n');
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                CloseList(builder, ref listOpen);
                continue;
            }

            if (TryReadHeading(trimmed, out var headingLevel, out var headingText))
            {
                CloseList(builder, ref listOpen);
                builder.Append("<h").Append(headingLevel).Append('>')
                    .Append(RenderInline(headingText))
                    .Append("</h").Append(headingLevel).AppendLine(">");
                continue;
            }

            if (TryReadBullet(trimmed, out var bulletText))
            {
                if (!listOpen)
                {
                    builder.AppendLine("<ul>");
                    listOpen = true;
                }

                builder.Append("<li>").Append(RenderInline(bulletText)).AppendLine("</li>");
                continue;
            }

            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                CloseList(builder, ref listOpen);
                builder.Append("<blockquote>").Append(RenderInline(trimmed.TrimStart('>', ' '))).AppendLine("</blockquote>");
                continue;
            }

            CloseList(builder, ref listOpen);
            builder.Append("<p>").Append(RenderInline(trimmed)).AppendLine("</p>");
        }

        CloseList(builder, ref listOpen);
        if (codeOpen)
        {
            builder.AppendLine("</code></pre>");
        }

        return builder.ToString();
    }

    private static void CloseList(StringBuilder builder, ref bool listOpen)
    {
        if (!listOpen)
        {
            return;
        }

        builder.AppendLine("</ul>");
        listOpen = false;
    }

    private static bool TryReadHeading(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;

        var match = HeadingRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }

        level = Math.Min(6, match.Groups["hashes"].Value.Length);
        text = match.Groups["text"].Value.Trim();
        return text.Length > 0;
    }

    private static bool TryReadBullet(string line, out string text)
    {
        text = string.Empty;
        if (line.Length < 3 || line[1] != ' ' || line[0] is not ('-' or '*'))
        {
            return false;
        }

        text = line[2..].Trim();
        return text.Length > 0;
    }

    private static string RenderInline(string value)
    {
        var encoded = WebUtility.HtmlEncode(value);
        encoded = InlineCodeRegex().Replace(encoded, "<code>$1</code>");
        encoded = BoldRegex().Replace(encoded, "<strong>$1</strong>");
        encoded = ItalicRegex().Replace(encoded, "<em>$1</em>");
        return encoded;
    }

    [GeneratedRegex("^(?<hashes>#{1,6})\\s+(?<text>.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex("\\*\\*([^*]+)\\*\\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex("(?<!\\*)\\*([^*]+)\\*(?!\\*)")]
    private static partial Regex ItalicRegex();
}
