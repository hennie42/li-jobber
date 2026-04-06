using System.Text.RegularExpressions;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using UglyToad.PdfPig;

namespace LiCvWriter.Infrastructure.Documents;

public sealed class InsightsDiscoveryPdfImporter : IInsightsDiscoveryPdfImporter
{
    public async Task<InsightsDiscoveryPdfImportResult> ImportAsync(Stream pdfStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdfStream);

        try
        {
            using var bufferedStream = new MemoryStream();
            await pdfStream.CopyToAsync(bufferedStream, cancellationToken);
            bufferedStream.Position = 0;

            using var document = PdfDocument.Open(bufferedStream);
            var pages = document.GetPages().ToArray();
            var extractedPages = new List<string>(pages.Length);

            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pageText = NormalizeText(page.Text);
                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    extractedPages.Add($"Page {page.Number}:{Environment.NewLine}{pageText}");
                }
            }

            var extractedText = string.Join(
                Environment.NewLine + Environment.NewLine,
                extractedPages);

            var warnings = new List<string>();
            if (pages.Length == 0)
            {
                warnings.Add("The uploaded PDF did not contain any readable pages.");
            }

            if (string.IsNullOrWhiteSpace(extractedText))
            {
                warnings.Add("No readable text could be extracted from the PDF. It may be image-only or scanned.");
            }
            else if (extractedText.Length < 250)
            {
                warnings.Add("Only a small amount of text was extracted from the PDF. Review the generated draft carefully.");
            }

            return new InsightsDiscoveryPdfImportResult(extractedText, pages.Length, warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("The uploaded file could not be read as a PDF.", exception);
        }
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => Regex.Replace(line, @"\s+", " ").Trim())
            .Where(static line => !string.IsNullOrWhiteSpace(line));

        return string.Join(Environment.NewLine, lines);
    }
}