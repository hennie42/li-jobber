using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
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

    private const string SampleRecommendationsMarkdown = """
# Alex Taylor

> Senior Architect

alex.taylor@example.com

## Target Role

- Role: Lead Architect
- Company: Contoso

## Recommendation Brief

External leaders consistently describe Alex as a pragmatic architecture partner.

## Recommendations

> *"Alex consistently turns complex cloud goals into useful delivery plans."*
> — Jane Smith at Contoso, CTO
""";

    private static void AssertVisibleContentOnlyPackage(string filePath, params string[] disallowedCorePropertiesText)
    {
        using (var archive = ZipFile.OpenRead(filePath))
        {
            Assert.DoesNotContain(archive.Entries,
                static entry => entry.FullName.StartsWith("customXml/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(archive.Entries, static entry => IsDisallowedHiddenPart(entry.FullName));

            foreach (var relationshipEntry in archive.Entries.Where(static entry => entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            {
                var relationshipsXml = ReadEntry(relationshipEntry);
                Assert.DoesNotContain("TargetMode=\"External\"", relationshipsXml, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("TargetMode='External'", relationshipsXml, StringComparison.OrdinalIgnoreCase);
            }

            var corePropertiesEntry = archive.GetEntry("docProps/core.xml");
            if (corePropertiesEntry is not null)
            {
                var corePropertiesXml = ReadEntry(corePropertiesEntry);
                Assert.DoesNotContain("<cp:keywords", corePropertiesXml, StringComparison.OrdinalIgnoreCase);

                foreach (var disallowed in disallowedCorePropertiesText.Where(static value => !string.IsNullOrWhiteSpace(value)))
                {
                    Assert.DoesNotContain(disallowed, corePropertiesXml, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        using var wordDoc = WordprocessingDocument.Open(filePath, isEditable: false);
        Assert.Empty(wordDoc.MainDocumentPart!.CustomXmlParts);
    }

    private static bool IsDisallowedHiddenPart(string fullName)
        => fullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)
            || fullName.Contains("/embeddings/", StringComparison.OrdinalIgnoreCase)
            || fullName.Contains("oleObject", StringComparison.OrdinalIgnoreCase)
            || fullName.Contains("/activeX/", StringComparison.OrdinalIgnoreCase)
            || fullName.Contains("afchunk", StringComparison.OrdinalIgnoreCase);

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

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
    public async Task ExportAsync_NonCvKindUsesApplicationMaterialTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.CoverLetter,
                "Alex Taylor Cover Letter",
                """
                # Alex Taylor

                > Senior Architect

                alex.taylor@example.com

                ## Target Role

                - Role: Lead Architect
                - Company: Contoso

                ## Cover Letter

                Dear hiring manager, I am applying for the role.
                """,
                "Alex Taylor",
                DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            Assert.NotNull(result.FilePath);
            var filePath = result.FilePath;

            Assert.True(File.Exists(filePath));
            AssertVisibleContentOnlyPackage(filePath, "Alex Taylor", "alex.taylor@example.com", "Lead Architect", "Contoso");

            using var wordDoc = WordprocessingDocument.Open(filePath, isEditable: false);
            var mainPart = wordDoc.MainDocumentPart;
            Assert.NotNull(mainPart);
            var body = mainPart.Document?.Body;
            Assert.NotNull(body);
            var allText = body.InnerText;

            Assert.Contains("Alex Taylor", allText);
            Assert.Contains("Lead Architect", allText);
            Assert.Contains("Dear hiring manager", allText);
            Assert.DoesNotContain("[Focused application material body]", allText);
            Assert.Empty(body.Descendants<SdtBlock>());
            Assert.Empty(body.Descendants<Table>());

            var headingParagraphs = body.Descendants<Paragraph>()
                .Where(p => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value is { } id
                    && id.StartsWith("Heading", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(headingParagraphs);

            var validationErrors = new OpenXmlValidator().Validate(wordDoc).ToArray();
            Assert.True(validationErrors.Length == 0,
                "Application material OpenXML validation errors:\n" + string.Join("\n", validationErrors.Select(static error =>
                    $"- {error.Part?.Uri}: {error.Path?.XPath}: {error.Description}")));
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
    public async Task ExportAsync_RecommendationsKindUsesRecommendationsTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.Recommendations,
                "Alex Taylor Recommendations",
                SampleRecommendationsMarkdown,
                "Alex Taylor Recommendations",
                DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            Assert.NotNull(result.FilePath);
            var filePath = result.FilePath;

            Assert.True(File.Exists(filePath));
            AssertVisibleContentOnlyPackage(filePath, "Alex Taylor", "alex.taylor@example.com", "Lead Architect", "Contoso", "Jane Smith");

            using var wordDoc = WordprocessingDocument.Open(filePath, isEditable: false);
            var mainPart = wordDoc.MainDocumentPart;
            Assert.NotNull(mainPart);
            var body = mainPart.Document?.Body;
            Assert.NotNull(body);
            var allText = body.InnerText;

            Assert.Contains("Alex Taylor", allText);
            Assert.Contains("Lead Architect", allText);
            Assert.Contains("External leaders", allText);
            Assert.Contains("Jane Smith", allText);
            Assert.Contains("complex cloud goals", allText);
            Assert.DoesNotContain("[Recommendation quotes", allText);
            Assert.Empty(body.Descendants<SdtBlock>());
            Assert.Empty(body.Descendants<Table>());

            var validationErrors = new OpenXmlValidator().Validate(wordDoc).ToArray();
            Assert.True(validationErrors.Length == 0,
                "Recommendations OpenXML validation errors:\n" + string.Join("\n", validationErrors.Select(static error =>
                    $"- {error.Part?.Uri}: {error.Path?.XPath}: {error.Description}")));
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

    private const string FullCvMarkdown = """
# Alex Taylor

> Senior Architect

alex.taylor@example.com · +45 00 00 00 00 · https://www.linkedin.com/in/alex-taylor-demo · Aarhus

## Target Role

- Role: Lead Architect
- Company: Contoso

## Professional Profile

Experienced architect with deep cloud delivery experience.

**Key Technologies & Competencies:** Azure, .NET, Kubernetes

## Professional Experience

### Lead Architect | Contoso

2020-2024

Led the cloud migration program.

## Projects

**Cloud Migration Portal**

Built a self-service migration portal.

## Education

- **MSc Computer Science** | Aarhus University (2008-2010)

## Certifications

- Azure Solutions Architect Expert

## Languages

Danish — Native, English — Professional

## Recommendations

**Pat Reviewer**, CTO

> Alex is exceptional.

## Early Career

- Junior Engineer | LegacyCorp (2004-2007)
""";

    [Fact]
    public async Task ExportAsync_CvKind_PopulatesAllSectionTags()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.Cv, "Alex Taylor CV", FullCvMarkdown, "Alex Taylor", DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            using var wordDoc = WordprocessingDocument.Open(result.FilePath!, isEditable: false);
            var body = wordDoc.MainDocumentPart!.Document!.Body!;
            var allText = body.InnerText;

            Assert.Contains("Alex Taylor", allText);            // CandidateHeader
            Assert.Contains("Experienced architect", allText);  // ProfileSummary
            Assert.Contains("Key Technologies & Competencies", allText); // KeySkills heading
            Assert.Contains("Azure", allText);                  // KeySkills
            Assert.Contains("Lead Architect", allText);         // Experience
            Assert.Contains("Cloud Migration Portal", allText); // Projects
            Assert.Contains("Aarhus University", allText);      // Education
            Assert.Contains("Azure Solutions Architect", allText); // Certifications
            Assert.Contains("Danish", allText);                 // Languages
            Assert.DoesNotContain("Pat Reviewer", allText);     // Recommendations are a separate document
            Assert.Contains("LegacyCorp", allText);             // EarlyCareer
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_CvKind_RemovesFitSnapshotControl()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.Cv, "Alex Taylor CV", FullCvMarkdown, "Alex Taylor", DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            using var wordDoc = WordprocessingDocument.Open(result.FilePath!, isEditable: false);
            var body = wordDoc.MainDocumentPart!.Document!.Body!;

            // After unwrap + cleanup no SDT remnants from the FitSnapshot tag
            // (or any other tag) should remain in the saved document body.
            Assert.Empty(body.Descendants<SdtBlock>());
            Assert.Empty(body.Descendants<SdtRun>());
            // FitSnapshot template placeholder text must not survive.
            Assert.DoesNotContain("Fit Snapshot", body.InnerText);
            Assert.DoesNotContain("[Strengths", body.InnerText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_CvKind_HeaderContainsContactFields()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.Cv, "Alex Taylor CV", FullCvMarkdown, "Alex Taylor", DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            using var wordDoc = WordprocessingDocument.Open(result.FilePath!, isEditable: false);
            var allText = wordDoc.MainDocumentPart!.Document!.Body!.InnerText;

            Assert.Contains("alex.taylor@example.com", allText);
            Assert.Contains("+45 00 00 00 00", allText);
            Assert.Contains("alex-taylor-demo", allText);
            Assert.Contains("Aarhus", allText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_CvKind_UsesHeadingStyleForSectionTitles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.Cv, "Alex Taylor CV", FullCvMarkdown, "Alex Taylor", DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            using var wordDoc = WordprocessingDocument.Open(result.FilePath!, isEditable: false);
            var body = wordDoc.MainDocumentPart!.Document!.Body!;

            // Each "## Section" heading from the markdown must resolve to a real
            // Heading style (Heading1/Heading2/...) so ATS parsers detect section
            // boundaries from style metadata, not from formatting heuristics.
            var headingParagraphs = body.Descendants<Paragraph>()
                .Where(p => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value is { } id
                    && id.StartsWith("Heading", StringComparison.Ordinal))
                .ToArray();

            Assert.NotEmpty(headingParagraphs);
            Assert.Contains(headingParagraphs, p =>
                p.InnerText.Contains("Key Technologies & Competencies", StringComparison.Ordinal));
            // At least Profile, KeySkills, Experience, Education, Certifications, Languages, Recommendations.
            Assert.True(headingParagraphs.Length >= 6,
                $"Expected at least 6 heading-styled paragraphs, found {headingParagraphs.Length}.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_CvKind_NoTablesInBody()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.Cv, "Alex Taylor CV", FullCvMarkdown, "Alex Taylor", DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            using var wordDoc = WordprocessingDocument.Open(result.FilePath!, isEditable: false);
            var body = wordDoc.MainDocumentPart!.Document!.Body!;

            // ATS parsers reliably mishandle tables: requirement is single-column,
            // paragraph-only output. Any w:tbl in the body breaks that contract.
            Assert.Empty(body.Descendants<Table>());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_CvKind_UsesVisibleContentOnlyPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.Cv, "Alex Taylor CV", FullCvMarkdown, "Alex Taylor", DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            AssertVisibleContentOnlyPackage(
                result.FilePath!,
                "Alex Taylor",
                "alex.taylor@example.com",
                "+45 00 00 00 00",
                "alex-taylor-demo",
                "Aarhus",
                "Lead Architect",
                "Contoso",
                "Azure",
                "Kubernetes",
                "candidateData",
                "urn:licvwriter:cv:v1");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_CvKind_ProducesNonTrivialDocxThatPassesOpenXmlValidation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-template-export-{Guid.NewGuid():N}");

        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = root });
            var document = new GeneratedDocument(
                DocumentKind.Cv, "Alex Taylor CV", FullCvMarkdown, "Alex Taylor", DateTimeOffset.UtcNow);

            var result = await service.ExportAsync(document);

            var fileInfo = new FileInfo(result.FilePath!);
            // Lower bound deliberately conservative: a .docx is a zip-compressed
            // package, so a populated CV typically lands ~5-10 KB. Anything below
            // 3 KB indicates the template wasn't populated at all.
            Assert.True(fileInfo.Length > 3_000,
                $"Expected a non-trivial CV docx (>3 KB), got {fileInfo.Length} bytes.");

            using (var archive = ZipFile.OpenRead(result.FilePath!))
            using (var contentTypesStream = archive.GetEntry("[Content_Types].xml")!.Open())
            using (var reader = new StreamReader(contentTypesStream))
            {
                var contentTypesXml = reader.ReadToEnd();
                Assert.DoesNotContain("wordprocessingml.template.main+xml", contentTypesXml);
                Assert.Contains("wordprocessingml.document.main+xml", contentTypesXml);
            }

            using var wordDoc = WordprocessingDocument.Open(result.FilePath!, isEditable: false);
            var validator = new OpenXmlValidator();
            var validationErrors = validator.Validate(wordDoc).ToArray();

            Assert.True(validationErrors.Length == 0,
                "CV OpenXML validation errors:\n" + string.Join("\n", validationErrors.Select(static error =>
                    $"- {error.Part?.Uri}: {error.Path?.XPath}: {error.Description}")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
