namespace LiCvWriter.Application.Models;

public sealed record InsightsDiscoveryPdfImportResult(
    string ExtractedText,
    int PageCount,
    IReadOnlyList<string> Warnings)
{
    public bool HasText => !string.IsNullOrWhiteSpace(ExtractedText);

    public int CharacterCount => ExtractedText.Length;
}