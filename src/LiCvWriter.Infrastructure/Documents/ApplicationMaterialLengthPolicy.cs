using System.Text;
using LiCvWriter.Core.Documents;

namespace LiCvWriter.Infrastructure.Documents;

internal static class ApplicationMaterialLengthPolicy
{
    public static GeneratedDocument Enforce(GeneratedDocument document)
    {
        var maxWordCount = GetMaxWordCount(document.Kind);
        if (maxWordCount is null || CountWords(document.Markdown) <= maxWordCount.Value)
        {
            return document;
        }

        var trimmed = TrimMarkdownToWordBudget(document.Markdown, maxWordCount.Value);
        return document with
        {
            Markdown = trimmed,
            PlainText = trimmed
        };
    }

    internal static int? GetMaxWordCount(DocumentKind kind) => kind switch
    {
        DocumentKind.CoverLetter => 380,
        DocumentKind.ProfileSummary => 180,
        DocumentKind.InterviewNotes => 450,
        _ => null
    };

    internal static int CountWords(string value)
    {
        var count = 0;
        var inWord = false;

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (!inWord)
                {
                    count++;
                    inWord = true;
                }
            }
            else
            {
                inWord = false;
            }
        }

        return count;
    }

    private static string TrimMarkdownToWordBudget(string markdown, int maxWordCount)
    {
        var lines = markdown.Split(["\r\n", "\n"], StringSplitOptions.None);
        var builder = new StringBuilder(markdown.Length);
        var remainingWords = maxWordCount;

        foreach (var line in lines)
        {
            if (remainingWords <= 0)
            {
                break;
            }

            var lineWordCount = CountWords(line);
            if (lineWordCount == 0)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                continue;
            }

            if (lineWordCount <= remainingWords)
            {
                builder.AppendLine(line);
                remainingWords -= lineWordCount;
                continue;
            }

            var trimmedLine = TrimLineToWordCount(line, remainingWords);
            builder.AppendLine(TrimAtSentenceBoundary(trimmedLine));
            break;
        }

        return builder.ToString().Trim();
    }

    private static string TrimLineToWordCount(string line, int maxWordCount)
    {
        if (maxWordCount <= 0)
        {
            return string.Empty;
        }

        var wordCount = 0;
        var inWord = false;
        var endIndex = 0;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (char.IsLetterOrDigit(character))
            {
                if (!inWord)
                {
                    wordCount++;
                    inWord = true;
                }

                if (wordCount <= maxWordCount)
                {
                    endIndex = index + 1;
                }
            }
            else
            {
                inWord = false;
                if (wordCount <= maxWordCount)
                {
                    endIndex = index + 1;
                }
            }

            if (wordCount > maxWordCount)
            {
                break;
            }
        }

        return line[..Math.Min(endIndex, line.Length)].TrimEnd(' ', ',', ';', ':', '-');
    }

    private static string TrimAtSentenceBoundary(string value)
    {
        var trimmed = value.TrimEnd();
        var boundaryIndex = trimmed.LastIndexOfAny(['.', '?', '!']);
        if (boundaryIndex >= trimmed.Length / 2)
        {
            return trimmed[..(boundaryIndex + 1)].TrimEnd();
        }

        return trimmed;
    }
}