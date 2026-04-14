using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HtmlToOpenXml;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Documents;
using Markdig;

namespace LiCvWriter.Infrastructure.Documents;

/// <summary>
/// Exports generated documents to local disk as Markdown and Word (.docx) files.
/// Word documents use built-in heading styles and a single-column layout for ATS/AI readability.
/// </summary>
public sealed class LocalDocumentExportService(StorageOptions options) : IDocumentExportService
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public async Task<DocumentExportResult> ExportAsync(GeneratedDocument document, CancellationToken cancellationToken = default)
    {
        var exportRoot = ExpandPath(options.ExportRoot);
        var exportFolder = ResolveExportFolder(exportRoot, document.OutputPath);
        Directory.CreateDirectory(exportFolder);

        var timestamp = document.GeneratedAtUtc.ToString("yyyyMMdd-HHmmss");
        var safeFileStem = SanitizeFileName($"{timestamp}-{document.Kind}-{document.Title}");

        var markdownPath = Path.Combine(exportFolder, $"{safeFileStem}.md");
        await File.WriteAllTextAsync(markdownPath, document.Markdown, Encoding.UTF8, cancellationToken);

        var wordPath = Path.Combine(exportFolder, $"{safeFileStem}.docx");
        GenerateWordDocument(document.Markdown, document.Title, document.Kind.ToString(), wordPath);

        return new DocumentExportResult(document.Kind, markdownPath, wordPath);
    }

    /// <summary>
    /// Generates a professional, ATS-friendly Word document from Markdown content.
    /// Uses built-in heading styles, single-column layout, Calibri font, and proper semantic structure.
    /// </summary>
    private static void GenerateWordDocument(string markdown, string title, string subject, string outputPath)
    {
        var html = Markdown.ToHtml(markdown, MarkdownPipeline);

        using var document = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);

        SetDocumentProperties(document, title, subject);

        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());

        AddStyleDefinitions(mainPart);
        AddDocumentDefaults(mainPart);

        var converter = new HtmlConverter(mainPart);
        converter.ParseBody(html);

        SetSingleColumnLayout(mainPart.Document.Body!);

        mainPart.Document.Save();
    }

    /// <summary>
    /// Sets document-level metadata (title, subject) for ATS metadata extraction.
    /// </summary>
    private static void SetDocumentProperties(WordprocessingDocument document, string title, string subject)
    {
        var properties = document.AddCoreFilePropertiesPart();
        using var stream = properties.GetStream(FileMode.Create);
        using var writer = new System.Xml.XmlTextWriter(stream, Encoding.UTF8);
        writer.WriteStartDocument();
        writer.WriteStartElement("cp", "coreProperties", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties");
        writer.WriteAttributeString("xmlns", "dc", null, "http://purl.org/dc/elements/1.1/");
        writer.WriteElementString("dc", "title", "http://purl.org/dc/elements/1.1/", title);
        writer.WriteElementString("dc", "subject", "http://purl.org/dc/elements/1.1/", subject);
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>
    /// Adds built-in Word heading and body styles for ATS compatibility.
    /// Uses Calibri font with professional sizing hierarchy.
    /// </summary>
    private static void AddStyleDefinitions(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();

        styles.Append(CreateHeadingStyle("Heading1", "heading 1", "28", true));
        styles.Append(CreateHeadingStyle("Heading2", "heading 2", "24", true));
        styles.Append(CreateHeadingStyle("Heading3", "heading 3", "22", true));
        styles.Append(CreateBodyStyle());

        stylesPart.Styles = styles;
        stylesPart.Styles.Save();
    }

    /// <summary>
    /// Sets document default font (Calibri 11pt) and line spacing (1.15).
    /// </summary>
    private static void AddDocumentDefaults(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.StyleDefinitionsPart;
        if (stylesPart?.Styles is null)
        {
            return;
        }

        var docDefaults = new DocDefaults(
            new RunPropertiesDefault(
                new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" },
                    new FontSize { Val = "22" })),
            new ParagraphPropertiesDefault(
                new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "120", Line = "276", LineRule = LineSpacingRuleValues.Auto })));

        stylesPart.Styles.PrependChild(docDefaults);
        stylesPart.Styles.Save();
    }

    /// <summary>
    /// Sets single-column page layout with standard margins for ATS readability.
    /// </summary>
    private static void SetSingleColumnLayout(Body body)
    {
        var sectionProperties = body.GetFirstChild<SectionProperties>() ?? body.AppendChild(new SectionProperties());

        sectionProperties.RemoveAllChildren<PageMargin>();
        sectionProperties.Append(new PageMargin
        {
            Top = 1440,      // 1 inch
            Right = 1440,
            Bottom = 1440,
            Left = 1440
        });
    }

    private static Style CreateHeadingStyle(string styleId, string styleName, string fontSize, bool bold)
    {
        return new Style(
            new StyleName { Val = styleName },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleRunProperties(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" },
                new Bold { Val = OnOffValue.FromBoolean(bold) },
                new FontSize { Val = fontSize },
                new Color { Val = "1F1F1F" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "240", After = "120" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = styleId
        };
    }

    private static Style CreateBodyStyle()
    {
        return new Style(
            new StyleName { Val = "Normal" },
            new StyleRunProperties(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" },
                new FontSize { Val = "22" },
                new Color { Val = "000000" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { After = "120", Line = "276", LineRule = LineSpacingRuleValues.Auto }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "Normal",
            Default = true
        };
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
}