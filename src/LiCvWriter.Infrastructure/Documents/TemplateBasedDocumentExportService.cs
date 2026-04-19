using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HtmlToOpenXml;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Documents;
using LiCvWriter.Infrastructure.Documents.Templates;
using Markdig;

namespace LiCvWriter.Infrastructure.Documents;

/// <summary>
/// Exports generated documents to disk as Word (<c>.docx</c>) files. CVs are
/// produced from the embedded <c>cv-template.dotx</c> by populating named
/// content controls per section. Other document kinds fall back to a direct
/// Markdown→HTML→OpenXml conversion that mirrors the template's font and
/// layout choices.
/// </summary>
public sealed class TemplateBasedDocumentExportService(StorageOptions options) : IDocumentExportService
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>
    /// Maps each tagged content control in <c>cv-template.dotx</c> to a markdown
    /// extractor that produces the section's content from the rendered CV
    /// markdown. Order matches the template's section ordering.
    /// </summary>
    private static readonly IReadOnlyList<CvSectionMapping> CvSectionMappings =
    [
        new("CandidateHeader", null, null, CvMarkdownSectionExtractor.ExtractCandidateHeader),
        new("ProfileSummary", CvSection.ProfileSummary, "Professional Profile", CvMarkdownSectionExtractor.ExtractProfileSummary),
        new("KeySkills", CvSection.KeySkills, "Key Technologies & Competencies", CvMarkdownSectionExtractor.ExtractKeySkills),
        new("FitSnapshot", null, null, markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Fit Snapshot", "Matchvurdering")),
        new("Experience", CvSection.ExperienceHighlights, "Professional Experience", markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Professional Experience", "Erhvervserfaring")),
        new("Projects", CvSection.ProjectHighlights, "Projects", markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Projects", "Projekter")),
        new("EarlyCareer", null, null, markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Early career", "Tidlig karriere")),
        new("Recommendations", null, null, markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Recommendations", "Anbefalinger")),
        new("Certifications", null, null, markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Certifications", "Certificeringer")),
    ];

    public async Task<DocumentExportResult> ExportAsync(GeneratedDocument document, CancellationToken cancellationToken = default)
    {
        var exportRoot = ExpandPath(options.ExportRoot);
        var exportFolder = ResolveExportFolder(exportRoot, document.OutputPath);
        Directory.CreateDirectory(exportFolder);

        var timestamp = document.GeneratedAtUtc.ToString("yyyyMMdd-HHmmss");
        var safeFileStem = SanitizeFileName($"{timestamp}-{document.Kind}-{document.Title}");
        var wordPath = Path.Combine(exportFolder, $"{safeFileStem}.docx");

        if (document.Kind is DocumentKind.Cv)
        {
            await Task.Run(() => GenerateFromTemplate(document, wordPath), cancellationToken);
        }
        else
        {
            await Task.Run(() => GenerateInline(document, wordPath), cancellationToken);
        }

        return new DocumentExportResult(document.Kind, wordPath);
    }
    /// <summary>
    /// Clones the embedded CV template, splits the rendered CV markdown into
    /// sections, populates each tagged content control, and removes any
    /// controls left empty (e.g. recommendations or early-career when absent).
    /// </summary>
    private static void GenerateFromTemplate(GeneratedDocument document, string outputPath)
    {
        // Copy embedded template into the destination so we never mutate the embedded original.
        using (var templateStream = EmbeddedTemplateProvider.OpenTemplate(EmbeddedTemplateProvider.CvTemplateResourceName))
        using (var destinationStream = File.Create(outputPath))
        {
            templateStream.CopyTo(destinationStream);
        }

        // .dotx files declare TemplateDocumentType in their content type. Switch
        // to a regular Document so Word opens it as an editable file rather than
        // creating a new document from the template.
        using (var package = WordprocessingDocument.Open(outputPath, isEditable: true))
        {
            package.ChangeDocumentType(WordprocessingDocumentType.Document);
        }

        using var wordDoc = WordprocessingDocument.Open(outputPath, isEditable: true);
        var mainPart = wordDoc.MainDocumentPart
            ?? throw new InvalidOperationException("CV template is missing its main document part.");

        SetCoreProperties(wordDoc, document);

        var populatedTags = new HashSet<string>(StringComparer.Ordinal);

        // Build a fast lookup of any LLM-generated sections attached to the
        // document so we can skip the markdown round-trip and feed the raw
        // section content straight into the matching content control.
        var generatedSectionLookup = document.GeneratedSections is null
            ? new Dictionary<CvSection, string>(0)
            : document.GeneratedSections
                .Where(s => !string.IsNullOrWhiteSpace(s.Markdown))
                .ToDictionary(s => s.Section, s => s.Markdown);

        foreach (var mapping in CvSectionMappings)
        {
            string? sectionMarkdown = null;
            var usedRawSection = false;
            if (mapping.GeneratedSection is { } gs && generatedSectionLookup.TryGetValue(gs, out var generated))
            {
                sectionMarkdown = generated;
                usedRawSection = true;
            }

            sectionMarkdown ??= mapping.Extract(document.Markdown);

            // When the raw LLM section is used directly (not extracted from the
            // assembled markdown), it lacks the "## Section Title" heading the
            // renderer normally prepends. Add it so the exported document has a
            // visible section heading inside each content control.
            if (usedRawSection && mapping.SectionHeading is not null && !string.IsNullOrWhiteSpace(sectionMarkdown))
            {
                sectionMarkdown = $"## {mapping.SectionHeading}\n\n{sectionMarkdown}";
            }

            if (TemplateContentPopulator.PopulateContentControl(mainPart, mapping.Tag, sectionMarkdown))
            {
                populatedTags.Add(mapping.Tag);
            }
        }

        var body = mainPart.Document?.Body
            ?? throw new InvalidOperationException("CV template body missing.");

        TemplateContentPopulator.RemoveEmptyControls(body, populatedTags);

        // Unwrap remaining structured-document-tag (SdtBlock) wrappers so the
        // saved document contains plain paragraphs/lists/headings. Many ATS
        // parsers (Workday, Taleo, Greenhouse) and most LLM-based CV parsers
        // treat SDT-wrapped content as opaque or skip it entirely.
        TemplateContentPopulator.UnwrapAllSdtBlocks(body);

        mainPart.Document!.Save();
    }

    /// <summary>
    /// Generates a minimal Word document for non-CV kinds by converting the full
    /// rendered markdown to HTML and emitting it directly. Mirrors the template's
    /// Calibri styling so output is visually consistent across document kinds.
    /// </summary>
    private static void GenerateInline(GeneratedDocument document, string outputPath)
    {
        var html = Markdown.ToHtml(document.Markdown, MarkdownPipeline);

        using var wordDoc = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
        SetCoreProperties(wordDoc, document);

        var mainPart = wordDoc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());

        AddCalibriStyles(mainPart);

        var converter = new HtmlConverter(mainPart);
        converter.ParseBody(html);

        AppendDefaultSection(mainPart.Document.Body!);
        mainPart.Document.Save();
    }

    private static void SetCoreProperties(WordprocessingDocument document, GeneratedDocument generated)
    {
        var properties = document.CoreFilePropertiesPart
            ?? document.AddCoreFilePropertiesPart();

        var keywords = string.Join(", ", new[]
            {
                generated.Title,
                generated.Kind.ToString()
            }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

        var description = $"{generated.Kind} generated by LiCvWriter on {generated.GeneratedAtUtc:yyyy-MM-dd}.";

        using var stream = properties.GetStream(FileMode.Create);
        using var writer = new System.Xml.XmlTextWriter(stream, System.Text.Encoding.UTF8);
        writer.WriteStartDocument();
        writer.WriteStartElement("cp", "coreProperties", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties");
        writer.WriteAttributeString("xmlns", "dc", null, "http://purl.org/dc/elements/1.1/");
        writer.WriteElementString("dc", "title", "http://purl.org/dc/elements/1.1/", generated.Title);
        writer.WriteElementString("dc", "subject", "http://purl.org/dc/elements/1.1/", generated.Kind.ToString());
        writer.WriteElementString("dc", "creator", "http://purl.org/dc/elements/1.1/", "LiCvWriter");
        writer.WriteElementString("dc", "description", "http://purl.org/dc/elements/1.1/", description);
        writer.WriteElementString("cp", "keywords", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties", keywords);
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void AddCalibriStyles(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(
                    new RunPropertiesBaseStyle(
                        new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" },
                        new FontSize { Val = "22" })),
                new ParagraphPropertiesDefault(
                    new ParagraphPropertiesBaseStyle(
                        new SpacingBetweenLines { After = "120", Line = "276", LineRule = LineSpacingRuleValues.Auto }))),
            CreateBodyStyle(),
            CreateHeadingStyle("Heading1", "heading 1", "28"),
            CreateHeadingStyle("Heading2", "heading 2", "24"),
            CreateHeadingStyle("Heading3", "heading 3", "22"));
        stylesPart.Styles.Save();
    }

    private static Style CreateBodyStyle()
        => new(
            new StyleName { Val = "Normal" },
            new StyleRunProperties(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" },
                new FontSize { Val = "22" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "Normal",
            Default = true
        };

    private static Style CreateHeadingStyle(string styleId, string styleName, string fontSize)
        => new(
            new StyleName { Val = styleName },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleRunProperties(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" },
                new Bold { Val = OnOffValue.FromBoolean(true) },
                new FontSize { Val = fontSize },
                new Color { Val = "1F1F1F" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "240", After = "120" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = styleId
        };

    private static void AppendDefaultSection(Body body)
    {
        var sectionProperties = body.GetFirstChild<SectionProperties>() ?? body.AppendChild(new SectionProperties());
        sectionProperties.RemoveAllChildren<PageMargin>();
        sectionProperties.Append(new PageMargin
        {
            Top = 1440,
            Right = 1440,
            Bottom = 1440,
            Left = 1440
        });
    }

    private static string ExpandPath(string path)
        => Environment.ExpandEnvironmentVariables(path.Replace('/', Path.DirectorySeparatorChar));

    private static string ResolveExportFolder(string exportRoot, string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return exportRoot;
        }

        var normalized = outputPath.Trim();
        return Path.IsPathRooted(normalized)
            ? normalized
            : Path.Combine(exportRoot, normalized);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '-' : character));
    }

    private sealed record CvSectionMapping(string Tag, CvSection? GeneratedSection, string? SectionHeading, Func<string, string?> Extract);
}
