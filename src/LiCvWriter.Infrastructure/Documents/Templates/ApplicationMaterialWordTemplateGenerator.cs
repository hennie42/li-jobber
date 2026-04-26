using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace LiCvWriter.Infrastructure.Documents.Templates;

/// <summary>
/// Generates the Word template used for focused non-CV application materials.
/// </summary>
public static class ApplicationMaterialWordTemplateGenerator
{
    private const string AccentColorHex = "2A5C8A";
    private const string PrimaryFont = "Aptos";
    private const string PrimaryFontFallback = "Calibri";

    public static readonly IReadOnlyList<ApplicationMaterialTemplateSection> Sections =
    [
        new("CandidateHeader", "Candidate Header", "[Candidate name, headline, and contact line]"),
        new("TargetRole", "Target Role", "[Target role and company]"),
        new("DocumentBody", "Document Body", "[Focused application material body]")
    ];

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
        AddFontTable(mainPart);

        var body = mainPart.Document.Body!;
        foreach (var section in Sections)
        {
            body.Append(CreateTaggedContentControl(section));
        }

        body.Append(BuildSectionProperties());
        mainPart.Document.Save();
    }

    private static SdtBlock CreateTaggedContentControl(ApplicationMaterialTemplateSection section)
        => new(
            new SdtProperties(
                new SdtAlias { Val = section.Title },
                new Tag { Val = section.Tag },
                new SdtId { Val = CreateStableSdtId(section.Tag) },
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

    private static int CreateStableSdtId(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= 16777619;
            }

            return (int)(hash & 0x7FFFFFFF);
        }
    }

    private static SectionProperties BuildSectionProperties()
        => new(
            new PageSize { Width = 11906, Height = 16838 },
            new PageMargin
            {
                Top = 1134,
                Right = 1134,
                Bottom = 1134,
                Left = 1134,
                Header = 720,
                Footer = 720,
                Gutter = 0
            },
            new Columns { Space = "720", ColumnCount = 1 },
            new DocGrid { LinePitch = 360 });

    private static void AddStyles(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            CreateBodyStyle(),
            CreateHeading1Style(),
            CreateHeading2Style(),
            CreateHeading3Style(),
            CreateContactLineStyle());
        stylesPart.Styles.Save();
    }

    private static void AddDocumentDefaults(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.StyleDefinitionsPart!;
        stylesPart.Styles!.PrependChild(new DocDefaults(
            new RunPropertiesDefault(
                new RunPropertiesBaseStyle(
                    BuildRunFonts(),
                    new FontSize { Val = "22" })),
            new ParagraphPropertiesDefault(
                new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines
                    {
                        After = "80",
                        Line = "276",
                        LineRule = LineSpacingRuleValues.Auto
                    }))));
        stylesPart.Styles.Save();
    }

    private static void AddFontTable(MainDocumentPart mainPart)
    {
        var fontTablePart = mainPart.AddNewPart<FontTablePart>();
        fontTablePart.Fonts = new Fonts(
            new Font(new AltName { Val = PrimaryFontFallback })
            {
                Name = PrimaryFont
            },
            new Font
            {
                Name = PrimaryFontFallback
            });
        fontTablePart.Fonts.Save();
    }

    private static RunFonts BuildRunFonts()
        => new()
        {
            Ascii = PrimaryFont,
            HighAnsi = PrimaryFont,
            ComplexScript = PrimaryFontFallback,
            EastAsia = PrimaryFontFallback
        };

    private static Style CreateBodyStyle()
        => new(
            new StyleName { Val = "Normal" },
            new StyleParagraphProperties(
                new SpacingBetweenLines
                {
                    After = "80",
                    Line = "276",
                    LineRule = LineSpacingRuleValues.Auto
                }),
            new StyleRunProperties(
                BuildRunFonts(),
                new Color { Val = "1F1F1F" },
                new FontSize { Val = "22" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "Normal",
            Default = true
        };

    private static Style CreateHeading1Style()
        => new(
            new StyleName { Val = "heading 1" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleParagraphProperties(
                new ParagraphBorders(
                    new BottomBorder
                    {
                        Val = BorderValues.Single,
                        Size = 6,
                        Space = 2,
                        Color = AccentColorHex
                    }),
                new SpacingBetweenLines { Before = "220", After = "100" }),
            new StyleRunProperties(
                BuildRunFonts(),
                new Bold { Val = OnOffValue.FromBoolean(true) },
                new Color { Val = AccentColorHex },
                new FontSize { Val = "30" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "Heading1"
        };

    private static Style CreateHeading2Style()
        => new(
            new StyleName { Val = "heading 2" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "160", After = "60" }),
            new StyleRunProperties(
                BuildRunFonts(),
                new Bold { Val = OnOffValue.FromBoolean(true) },
                new Color { Val = "1F1F1F" },
                new FontSize { Val = "26" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "Heading2"
        };

    private static Style CreateHeading3Style()
        => new(
            new StyleName { Val = "heading 3" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "100", After = "40" }),
            new StyleRunProperties(
                BuildRunFonts(),
                new Bold { Val = OnOffValue.FromBoolean(true) },
                new Color { Val = "2A2A2A" },
                new FontSize { Val = "24" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "Heading3"
        };

    private static Style CreateContactLineStyle()
        => new(
            new StyleName { Val = "Contact Line" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "0", After = "180" }),
            new StyleRunProperties(
                BuildRunFonts(),
                new Color { Val = "4A4A4A" },
                new FontSize { Val = "20" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "ContactLine"
        };
}

/// <summary>
/// Describes a non-CV application material section exposed as a named content control.
/// </summary>
public sealed record ApplicationMaterialTemplateSection(string Tag, string Title, string Placeholder);