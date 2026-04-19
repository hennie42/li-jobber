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

            Assert.EndsWith(".docx", result.FilePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(result.FilePath));

            var folderFiles = Directory.GetFiles(Path.GetDirectoryName(result.FilePath)!);
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

            using var wordDoc = WordprocessingDocument.Open(result.FilePath, isEditable: false);
            var mainPart = wordDoc.MainDocumentPart;
            Assert.NotNull(mainPart);
            var body = mainPart.Document?.Body;
            Assert.NotNull(body);
            var allText = body.InnerText;

            Assert.Contains("Alex Taylor", allText);
            Assert.Contains("Lead Architect", allText);
            Assert.Contains("Experienced architect", allText);
            Assert.Contains("Azure", allText);
            Assert.Contains("Cloud architecture", allText);
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

            using var wordDoc = WordprocessingDocument.Open(result.FilePath, isEditable: false);
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

            Assert.True(File.Exists(result.FilePath));
            using var wordDoc = WordprocessingDocument.Open(result.FilePath, isEditable: false);
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

            Assert.StartsWith(
                Path.Combine(root, "job-set-01-contoso-lead-architect"),
                result.FilePath,
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

            using var wordDoc = WordprocessingDocument.Open(result.FilePath, isEditable: false);
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
}
