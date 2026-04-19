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
        new("CandidateHeader", CvMarkdownSectionExtractor.ExtractCandidateHeader),
        new("ProfileSummary", CvMarkdownSectionExtractor.ExtractProfileSummary),
        new("KeySkills", CvMarkdownSectionExtractor.ExtractKeySkills),
        new("FitSnapshot", markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Fit Snapshot", "Matchvurdering")),
        new("Experience", markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Professional Experience", "Erhvervserfaring")),
        new("Projects", markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Projects", "Projekter")),
        new("EarlyCareer", markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Early career", "Tidlig karriere")),
        new("Recommendations", markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Recommendations", "Anbefalinger")),
        new("Certifications", markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Certifications", "Certificeringer")),
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

        SetCoreProperties(wordDoc, document.Title, document.Kind.ToString());

        var populatedTags = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mapping in CvSectionMappings)
        {
            var sectionMarkdown = mapping.Extract(document.Markdown);
            if (TemplateContentPopulator.PopulateContentControl(mainPart, mapping.Tag, sectionMarkdown))
            {
                populatedTags.Add(mapping.Tag);
            }
        }

        TemplateContentPopulator.RemoveEmptyControls(
            mainPart.Document?.Body ?? throw new InvalidOperationException("CV template body missing."),
            populatedTags);

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
        SetCoreProperties(wordDoc, document.Title, document.Kind.ToString());

        var mainPart = wordDoc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());

        AddCalibriStyles(mainPart);

        var converter = new HtmlConverter(mainPart);
        converter.ParseBody(html);

        AppendDefaultSection(mainPart.Document.Body!);
        mainPart.Document.Save();
    }

    private static void SetCoreProperties(WordprocessingDocument document, string title, string subject)
    {
        var properties = document.CoreFilePropertiesPart
            ?? document.AddCoreFilePropertiesPart();

        using var stream = properties.GetStream(FileMode.Create);
        using var writer = new System.Xml.XmlTextWriter(stream, System.Text.Encoding.UTF8);
        writer.WriteStartDocument();
        writer.WriteStartElement("cp", "coreProperties", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties");
        writer.WriteAttributeString("xmlns", "dc", null, "http://purl.org/dc/elements/1.1/");
        writer.WriteElementString("dc", "title", "http://purl.org/dc/elements/1.1/", title);
        writer.WriteElementString("dc", "subject", "http://purl.org/dc/elements/1.1/", subject);
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

    private sealed record CvSectionMapping(string Tag, Func<string, string?> Extract);
}
