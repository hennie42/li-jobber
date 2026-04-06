using System.Text;
using LiCvWriter.Infrastructure.Documents;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class InsightsDiscoveryPdfImporterTests
{
    [Fact]
    public async Task ImportAsync_ExtractsReadableTextAndPageCount()
    {
        var importer = new InsightsDiscoveryPdfImporter();
        await using var stream = new MemoryStream(CreateSinglePagePdf("Insightful collaborator and pragmatic problem solver."));

        var result = await importer.ImportAsync(stream);

        Assert.True(result.HasText);
        Assert.Equal(1, result.PageCount);
        Assert.Contains("Insightful collaborator", result.ExtractedText);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("No readable text", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportAsync_WithoutReadableText_ReturnsWarning()
    {
        var importer = new InsightsDiscoveryPdfImporter();
        await using var stream = new MemoryStream(CreateSinglePagePdf(string.Empty));

        var result = await importer.ImportAsync(stream);

        Assert.False(result.HasText);
        Assert.Equal(1, result.PageCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("No readable text", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportAsync_WithInvalidPdf_ThrowsHelpfulError()
    {
        var importer = new InsightsDiscoveryPdfImporter();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not-a-pdf"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(stream));

        Assert.Contains("could not be read as a PDF", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] CreateSinglePagePdf(string text)
    {
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            BuildContentObject(text),
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n"
        };

        var builder = new StringBuilder();
        builder.Append("%PDF-1.4\n");

        var offsets = new List<int> { 0 };
        foreach (var value in objects)
        {
            offsets.Add(builder.Length);
            builder.Append(value);
        }

        var xrefOffset = builder.Length;
        builder.Append("xref\n0 6\n");
        builder.Append("0000000000 65535 f \n");

        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10"));
            builder.Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Root 1 0 R /Size 6 >>\n");
        builder.Append("startxref\n");
        builder.Append(xrefOffset);
        builder.Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string BuildContentObject(string text)
    {
        var escapedText = text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
        var stream = $"BT\n/F1 12 Tf\n72 720 Td\n({escapedText}) Tj\nET\n";
        return $"4 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream\nendobj\n";
    }
}