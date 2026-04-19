using LiCvWriter.Core.Documents;

namespace LiCvWriter.Infrastructure.Documents;

public sealed record CvQualityValidationResult(
    GeneratedDocument Document,
    CvQualityReport Report)
{
    public static CvQualityValidationResult Unchanged(GeneratedDocument document)
        => new(document, CvQualityReport.Empty);
}

public sealed record CvQualityReport(
    int MissingMustHaveThemeCount,
    int QuantifiedBulletCount,
    bool SummaryTrimmed,
    bool SectionOrderChanged,
    IReadOnlyList<string> TrimmedOptionalSections,
    IReadOnlyList<string> AppliedFixes,
    IReadOnlyList<string> MissingMustHaveThemes = null!,
    int AtsKeywordCoveragePercent = 0,
    int EstimatedPageCount = 0,
    IReadOnlyList<KeywordDensityEntry> KeywordDensity = null!)
{
    public static CvQualityReport Empty { get; } = new(0, 0, false, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), 0, 0, Array.Empty<KeywordDensityEntry>());

    /// <summary>
    /// Generates human-readable actionable feedback lines summarizing the quality report.
    /// </summary>
    public IReadOnlyList<string> BuildActionableFeedback()
    {
        var feedback = new List<string>();

        if (AtsKeywordCoveragePercent < 100 && MissingMustHaveThemes is { Count: > 0 })
        {
            feedback.Add($"ATS keyword coverage: {AtsKeywordCoveragePercent}%. Missing must-have themes: {string.Join(", ", MissingMustHaveThemes)}.");
        }
        else if (AtsKeywordCoveragePercent == 100)
        {
            feedback.Add("All must-have keywords are present in the CV.");
        }

        if (QuantifiedBulletCount < 3)
        {
            feedback.Add($"Only {QuantifiedBulletCount} bullet(s) contain numbers or metrics. Aim for 3+ quantified achievements.");
        }

        if (EstimatedPageCount > 4)
        {
            feedback.Add($"Estimated {EstimatedPageCount} pages. Consider trimming to 2-4 pages for readability.");
        }

        if (KeywordDensity is { Count: > 0 })
        {
            var lowDensity = KeywordDensity
                .Where(static e => e.IsCovered && e.Occurrences == 1)
                .Select(static e => e.Keyword)
                .ToArray();

            if (lowDensity.Length > 0)
            {
                feedback.Add($"Keywords appearing only once (consider reinforcing): {string.Join(", ", lowDensity)}.");
            }
        }

        if (AppliedFixes.Count > 0)
        {
            feedback.Add($"Auto-fixes applied: {string.Join(", ", AppliedFixes)}.");
        }

        return feedback;
    }
}

/// <summary>
/// Tracks how many times a must-have keyword appears in the generated CV text.
/// </summary>
public sealed record KeywordDensityEntry(string Keyword, int Occurrences, bool IsCovered);
