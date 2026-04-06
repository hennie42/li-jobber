using LiCvWriter.Application.Options;
using LiCvWriter.Core.Documents;
using LiCvWriter.Infrastructure.Documents;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class LocalDocumentExportServiceTests
{
    [Fact]
    public async Task ExportAsync_WritesMarkdownOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-export-{Guid.NewGuid():N}");

        try
        {
            var service = new LocalDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.Cv,
                "Alex Taylor CV",
                "# Alex Taylor\n\n## Summary\n\nSenior architect.",
                "Alex Taylor\nSummary\nSenior architect.",
                DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            Assert.True(File.Exists(result.MarkdownPath));
            Assert.EndsWith(".md", result.MarkdownPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".docx", result.MarkdownPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExportAsync_UsesPerJobOutputFolderWhenProvided()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-export-{Guid.NewGuid():N}");

        try
        {
            var service = new LocalDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.Cv,
                "Alex Taylor CV",
                "# Alex Taylor\n\n## Summary\n\nSenior architect.",
                "Alex Taylor\nSummary\nSenior architect.",
                DateTimeOffset.UtcNow,
                OutputPath: "job-set-01-contoso-lead-architect");

            var result = await service.ExportAsync(document);

            Assert.StartsWith(Path.Combine(root, "job-set-01-contoso-lead-architect"), result.MarkdownPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}