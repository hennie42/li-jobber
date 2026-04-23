using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Documents;
using LiCvWriter.Infrastructure.Documents;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class TemplateBasedDocumentExportServiceTests
{
    private const string SampleCvMarkdown = """
# Alex Taylor

> Senior Architect

## Target Role

- Role: Lead Architect
- Company: Contoso

## Professional Profile

Experienced architect with deep cloud delivery experience.

**Key Technologies & Competencies:** Azure, .NET, Kubernetes

## Fit Snapshot

- Strength: Cloud architecture

## Professional Experience

### Lead Architect | Contoso

2020-2024

Led the cloud migration program.
""";

    [Fact]
    public async Task ExportAsync_WritesOnlyDocxFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.Cv,
                "Alex Taylor CV",
                SampleCvMarkdown,
                "Alex Taylor",
                DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            Assert.NotNull(result.FilePath);
            var filePath = result.FilePath;

            Assert.EndsWith(".docx", filePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(filePath));

            var folderFiles = Directory.GetFiles(Path.GetDirectoryName(filePath)!);
            Assert.DoesNotContain(folderFiles, file => file.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
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
    public async Task ExportAsync_PopulatesContentControlsForCv()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.Cv,
                "Alex Taylor CV",
                SampleCvMarkdown,
                "Alex Taylor",
                DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            Assert.NotNull(result.FilePath);
            var filePath = result.FilePath;

            using var wordDoc = WordprocessingDocument.Open(filePath, isEditable: false);
            var mainPart = wordDoc.MainDocumentPart;
            Assert.NotNull(mainPart);
            var body = mainPart.Document?.Body;
            Assert.NotNull(body);
            var allText = body.InnerText;

            Assert.Contains("Alex Taylor", allText);
            Assert.Contains("Lead Architect", allText);
            Assert.Contains("Experienced architect", allText);
            Assert.Contains("Azure", allText);
            // FitSnapshot is intentionally excluded from the visible CV — internal
            // assessment content must not appear in the document sent to recruiters.
            Assert.DoesNotContain("Cloud architecture", allText);
            Assert.Contains("cloud migration", allText);
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
    public async Task ExportAsync_RemovesEmptyContentControlsFromCvTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });
            // Markdown intentionally omits Recommendations, Certifications, Projects, EarlyCareer.
            var document = new GeneratedDocument(
                DocumentKind.Cv,
                "Alex Taylor CV",
                SampleCvMarkdown,
                "Alex Taylor",
                DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            Assert.NotNull(result.FilePath);
            var filePath = result.FilePath;

            using var wordDoc = WordprocessingDocument.Open(filePath, isEditable: false);
            var mainPart = wordDoc.MainDocumentPart;
            Assert.NotNull(mainPart);
            var body = mainPart.Document?.Body;
            Assert.NotNull(body);
            var allText = body.InnerText;

            // Placeholder text from the template should not survive when the section was empty.
            Assert.DoesNotContain("[Recommendation quotes", allText);
            Assert.DoesNotContain("[Selected certifications", allText);
            Assert.DoesNotContain("[Project entries", allText);
            Assert.DoesNotContain("[Pre-cutoff roles", allText);
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
    public async Task ExportAsync_NonCvKindGeneratesInlineDocx()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.CoverLetter,
                "Alex Taylor Cover Letter",
                "# Alex Taylor\n\n## Letter\n\nDear hiring manager, I am applying for the role.",
                "Alex Taylor",
                DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            Assert.NotNull(result.FilePath);
            var filePath = result.FilePath;

            Assert.True(File.Exists(filePath));
            using var wordDoc = WordprocessingDocument.Open(filePath, isEditable: false);
            var mainPart = wordDoc.MainDocumentPart;
            Assert.NotNull(mainPart);
            var body = mainPart.Document?.Body;
            Assert.NotNull(body);
            Assert.Contains("Dear hiring manager", body.InnerText);
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
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.Cv,
                "Alex Taylor CV",
                SampleCvMarkdown,
                "Alex Taylor",
                DateTimeOffset.UtcNow,
                OutputPath: "job-set-01-contoso-lead-architect");

            var result = await service.ExportAsync(document);

            Assert.NotNull(result.FilePath);
            var filePath = result.FilePath;

            Assert.StartsWith(
                Path.Combine(root, "job-set-01-contoso-lead-architect"),
                filePath,
                StringComparison.OrdinalIgnoreCase);
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
    public async Task ExportAsync_UsesGeneratedSectionsDirectlyWhenAttached()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });

            // Markdown intentionally lacks the LLM-generated phrasing so we can prove
            // the section content (not the markdown body) reached the template.
            const string sparseMarkdown = """
# Alex Taylor

> Senior Architect

## Target Role

- Role: Lead Architect
- Company: Contoso

## Professional Profile

Generic placeholder body.

## Professional Experience

### Lead Architect | Contoso

2020-2024
""";

            var document = new GeneratedDocument(
                DocumentKind.Cv,
                "Alex Taylor CV",
                sparseMarkdown,
                "Alex Taylor",
                DateTimeOffset.UtcNow,
                GeneratedSections:
                [
                    new CvSectionMarkdown(CvSection.ProfileSummary, "Section-driven profile body for Lead Architect role."),
                    new CvSectionMarkdown(CvSection.KeySkills, "Azure, Kubernetes, Terraform"),
                    new CvSectionMarkdown(CvSection.ExperienceHighlights,
                        "### Lead Architect | Contoso\n\n2020-2024\n\n- Cut migration time 40% via infra-as-code.")
                ]);

            var result = await service.ExportAsync(document);

            Assert.NotNull(result.FilePath);
            var filePath = result.FilePath;

            using var wordDoc = WordprocessingDocument.Open(filePath, isEditable: false);
            var mainPart = wordDoc.MainDocumentPart;
            Assert.NotNull(mainPart);
            var body = mainPart.Document?.Body;
            Assert.NotNull(body);
            var allText = body.InnerText;

            Assert.Contains("Section-driven profile body for Lead Architect role.", allText);
            Assert.Contains("Azure, Kubernetes, Terraform", allText);
            Assert.Contains("Cut migration time 40% via infra-as-code.", allText);
            Assert.DoesNotContain("Generic placeholder body.", allText);
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
    public async Task ExportAsync_UnwrapsSdtBlocksAndNormalizesMalformedBullets()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });

            // ExperienceHighlights uses the malformed pattern observed from
            // local LLMs: bullets glued to the previous sentence with "*".
            const string malformedExperience =
                "### Senior Architect | Contoso.*Architected the cloud migration program.*Drove API-first adoption across 12 teams.";

            var document = new GeneratedDocument(
                DocumentKind.Cv,
                "Alex Taylor CV",
                SampleCvMarkdown,
                "Alex Taylor",
                DateTimeOffset.UtcNow,
                GeneratedSections:
                [
                    new CvSectionMarkdown(CvSection.ExperienceHighlights, malformedExperience)
                ]);

            var result = await service.ExportAsync(document);

            Assert.NotNull(result.FilePath);
            var filePath = result.FilePath;

            using var wordDoc = WordprocessingDocument.Open(filePath, isEditable: false);
            var mainPart = wordDoc.MainDocumentPart;
            Assert.NotNull(mainPart);
            var body = mainPart.Document?.Body;
            Assert.NotNull(body);

            // No SdtBlock wrappers should remain — the document must be plain
            // paragraphs/lists for ATS and LLM-based parsers.
            Assert.Empty(body.Descendants<SdtBlock>());

            // Bullets must render as a real list with NumberingProperties,
            // not as a single paragraph with literal "*" characters.
            var listParagraphs = body.Descendants<Paragraph>()
                .Where(p => p.ParagraphProperties?.NumberingProperties is not null
                    || p.ParagraphProperties?.ParagraphStyleId?.Val?.Value == "ListParagraph")
                .ToArray();
            Assert.NotEmpty(listParagraphs);

            // Heading paragraphs from the rewritten "### ..." line must use a
            // real Heading style id.
            var headingParagraphs = body.Descendants<Paragraph>()
                .Where(p => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value is { } id
                    && id.StartsWith("Heading", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(headingParagraphs);

            var allText = body.InnerText;
            Assert.DoesNotContain(".*", allText);
            Assert.Contains("Architected the cloud migration program", allText);
            Assert.Contains("Drove API-first adoption across 12 teams", allText);
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
