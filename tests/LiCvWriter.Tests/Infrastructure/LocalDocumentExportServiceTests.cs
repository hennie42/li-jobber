using DocumentFormat.OpenXml.Packaging;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Documents;
using LiCvWriter.Infrastructure.Documents;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class LocalDocumentExportServiceTests
{
    [Fact]
    public async Task ExportAsync_WritesMarkdownAndWordFiles()
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
            Assert.NotNull(result.WordPath);
            Assert.True(File.Exists(result.WordPath));
            Assert.EndsWith(".docx", result.WordPath, StringComparison.OrdinalIgnoreCase);
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
    public async Task ExportAsync_WordDocumentIsValidOpenXml()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-export-{Guid.NewGuid():N}");

        try
        {
            var service = new LocalDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.Cv,
                "Alex Taylor CV",
                "# Alex Taylor\n\n## Professional Profile\n\nExperienced architect.\n\n## Professional Experience\n\n### Lead Architect | Contoso\n2020-2024\n\nLed cloud migration.",
                "Alex Taylor",
                DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            using var wordDoc = WordprocessingDocument.Open(result.WordPath!, isEditable: false);
            var mainPart = wordDoc.MainDocumentPart;
            Assert.NotNull(mainPart);
            var body = mainPart!.Document?.Body;
            Assert.NotNull(body);

            var bodyText = body!.InnerText;
            Assert.Contains("Alex Taylor", bodyText);
            Assert.Contains("Experienced architect", bodyText);
            Assert.Contains("Lead Architect", bodyText);
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
    public async Task ExportAsync_WordDocumentHasBuiltInStyles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-export-{Guid.NewGuid():N}");

        try
        {
            var service = new LocalDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.Cv,
                "Style Test",
                "# Heading One\n\n## Heading Two\n\nBody text.",
                "Style Test",
                DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            using var wordDoc = WordprocessingDocument.Open(result.WordPath!, isEditable: false);
            var stylesPart = wordDoc.MainDocumentPart!.StyleDefinitionsPart;
            Assert.NotNull(stylesPart);

            var styleIds = stylesPart.Styles!.Elements<DocumentFormat.OpenXml.Wordprocessing.Style>()
                .Select(static s => s.StyleId?.Value)
                .ToArray();

            Assert.Contains("Heading1", styleIds);
            Assert.Contains("Heading2", styleIds);
            Assert.Contains("Normal", styleIds);
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
            Assert.NotNull(result.WordPath);
            Assert.StartsWith(Path.Combine(root, "job-set-01-contoso-lead-architect"), result.WordPath, StringComparison.OrdinalIgnoreCase);
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