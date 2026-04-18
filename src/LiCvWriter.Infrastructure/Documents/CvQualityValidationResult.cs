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
    IReadOnlyList<string> AppliedFixes)
{
    public static CvQualityReport Empty { get; } = new(0, 0, false, false, Array.Empty<string>(), Array.Empty<string>());
}
