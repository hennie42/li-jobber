using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace LiCvWriter.Infrastructure.Documents.Templates;

/// <summary>
/// Generates a reusable Word template (.dotx) for CV exports.
/// The template defines the visual design (Calibri styles, single-column layout,
/// 1-inch margins) and exposes named content controls for each CV section so the
/// export pipeline can populate sections individually.
/// </summary>
public static class CvWordTemplateGenerator
{
    /// <summary>
    /// Ordered set of CV sections rendered into the template as tagged
    /// <see cref="SdtBlock"/> content controls. The <c>Tag</c> value is the
    /// stable identifier the export pipeline uses to locate the placeholder.
    /// </summary>
    public static readonly IReadOnlyList<CvTemplateSection> Sections =
    [
        new("CandidateHeader", "Candidate Header", "[Candidate name, headline, and target role]"),
        new("ProfileSummary", "Professional Profile", "[Professional profile overview]"),
        new("KeySkills", "Key Technologies & Competencies", "[Comma-separated keyword line]"),
        new("FitSnapshot", "Fit Snapshot", "[Strengths and overall fit score]"),
        new("Experience", "Professional Experience", "[Recent roles with title, company, period, and achievements]"),
        new("Projects", "Projects", "[Project entries with period, description, and links]"),
        new("EarlyCareer", "Early Career", "[Pre-cutoff roles and projects]"),
        new("Certifications", "Certifications", "[Selected certifications]"),
        new("Recommendations", "Recommendations", "[Recommendation quotes with attribution]"),
    ];

    /// <summary>
    /// Generates the CV Word template at <paramref name="outputPath"/>.
    /// The output is a <c>.dotx</c> file with built-in heading styles, a
    /// Calibri body font, single-column 1-inch-margin layout, and one tagged
    /// content control per CV section.
    /// </summary>
    public static void Generate(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var document = WordprocessingDocument.Create(
            outputPath,
            WordprocessingDocumentType.Template);

        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());

        AddStyles(mainPart);
        AddDocumentDefaults(mainPart);

        var body = mainPart.Document.Body!;

        foreach (var section in Sections)
        {
            body.Append(CreateTaggedContentControl(section));
        }

        body.Append(BuildSectionProperties());

        mainPart.Document.Save();
    }

    private static SdtBlock CreateTaggedContentControl(CvTemplateSection section)
    {
        return new SdtBlock(
            new SdtProperties(
                new SdtAlias { Val = section.Title },
                new Tag { Val = section.Tag },
                new SdtId { Val = (int)(uint)section.Tag.GetHashCode() },
                new ShowingPlaceholder()),
            new SdtContentBlock(
                new Paragraph(
                    new ParagraphProperties(
                        new ParagraphStyleId { Val = "Normal" }),
                    new Run(
                        new RunProperties(
                            new Italic(),
                            new Color { Val = "808080" }),
                        new Text(section.Placeholder) { Space = SpaceProcessingModeValues.Preserve }))));
    }

    private static SectionProperties BuildSectionProperties()
    {
        return new SectionProperties(
            new PageSize { Width = 12240, Height = 15840 },
            new PageMargin
            {
                Top = 864,   // 0.6 in
                Right = 1080, // 0.75 in
                Bottom = 864, // 0.6 in
                Left = 1080,  // 0.75 in
                Header = 432,
                Footer = 432,
                Gutter = 0
            },
            new Columns { Space = "720", ColumnCount = 1 },
            new DocGrid { LinePitch = 360 });
    }

    private static void AddStyles(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            CreateBodyStyle(),
            CreateHeadingStyle("Heading1", "heading 1", fontSize: "26"),
            CreateHeadingStyle("Heading2", "heading 2", fontSize: "22"),
            CreateHeadingStyle("Heading3", "heading 3", fontSize: "20"));
        stylesPart.Styles.Save();
    }

    private static void AddDocumentDefaults(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.StyleDefinitionsPart!;
        stylesPart.Styles!.PrependChild(new DocDefaults(
            new RunPropertiesDefault(
                new RunPropertiesBaseStyle(
                    new RunFonts
                    {
                        Ascii = "Calibri",
                        HighAnsi = "Calibri",
                        ComplexScript = "Calibri"
                    },
                    new FontSize { Val = "20" })),
            new ParagraphPropertiesDefault(
                new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines
                    {
                        After = "40",
                        Line = "240",
                        LineRule = LineSpacingRuleValues.Auto
                    }))));
        stylesPart.Styles.Save();
    }

    private static Style CreateBodyStyle()
    {
        return new Style(
            new StyleName { Val = "Normal" },
            new StyleRunProperties(
                new RunFonts
                {
                    Ascii = "Calibri",
                    HighAnsi = "Calibri",
                    ComplexScript = "Calibri"
                },
                new FontSize { Val = "20" },
                new Color { Val = "000000" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines
                {
                    After = "40",
                    Line = "240",
                    LineRule = LineSpacingRuleValues.Auto
                }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "Normal",
            Default = true
        };
    }

    private static Style CreateHeadingStyle(string styleId, string styleName, string fontSize)
    {
        return new Style(
            new StyleName { Val = styleName },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleRunProperties(
                new RunFonts
                {
                    Ascii = "Calibri",
                    HighAnsi = "Calibri",
                    ComplexScript = "Calibri"
                },
                new Bold { Val = OnOffValue.FromBoolean(true) },
                new FontSize { Val = fontSize },
                new Color { Val = "1F1F1F" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "120", After = "40" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = styleId
        };
    }
}

/// <summary>
/// Describes a single CV section exposed in the Word template as a named
/// content control.
/// </summary>
/// <param name="Tag">Stable identifier used by the export pipeline to locate the placeholder.</param>
/// <param name="Title">Human-readable alias shown in Word's content control UI.</param>
/// <param name="Placeholder">Greyed-out placeholder text shown when the control is empty.</param>
public sealed record CvTemplateSection(string Tag, string Title, string Placeholder);
