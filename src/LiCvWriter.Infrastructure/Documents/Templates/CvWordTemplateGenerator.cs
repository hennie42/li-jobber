using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace LiCvWriter.Infrastructure.Documents.Templates;

/// <summary>
/// Generates the ATS-optimized, Danish/EU-friendly Word template (.dotx) for
/// CV exports. The template is single-column, uses real Heading styles, a
/// subtle accent colour under section headings, and exposes one named content
/// control per CV section so the export pipeline can populate sections
/// individually.
/// </summary>
/// <remarks>
/// Design decisions:
/// <list type="bullet">
///   <item><description>A4 page, 2 cm margins (EU standard).</description></item>
///   <item><description>Aptos 11 pt body with Calibri fallback so pre-Office-2024 installs
///     substitute cleanly without layout shifts.</description></item>
///   <item><description>Real Heading 1/2/3 styles — ATS parsers and LLM-based CV readers
///     rely on style semantics to detect sections.</description></item>
///   <item><description>Thin accent-coloured bottom border on Heading 1 for visual polish,
///     ignored by ATS, surviving in Word and LibreOffice.</description></item>
///   <item><description>No tables, no text boxes, no multi-column layout — the top three
///     ATS parsing failure modes.</description></item>
///   <item><description>No <c>FitSnapshot</c> section — internal assessment data must not
///     appear in the CV sent to recruiters.</description></item>
/// </list>
/// </remarks>
public static class CvWordTemplateGenerator
{
    /// <summary>Accent colour used for Heading 1 text and its bottom border.</summary>
    private const string AccentColorHex = "2A5C8A";

    /// <summary>Primary body font.</summary>
    private const string PrimaryFont = "Aptos";

    /// <summary>
    /// Fallback font paired with <see cref="PrimaryFont"/> in the complex-script
    /// and east-Asian slots. Older Office installs that ship without Aptos
    /// substitute cleanly because both fonts share comparable metrics.
    /// </summary>
    private const string PrimaryFontFallback = "Calibri";

    /// <summary>
    /// Ordered set of CV sections rendered into the template as tagged
    /// <see cref="SdtBlock"/> content controls. The <c>Tag</c> value is the
    /// stable identifier the export pipeline uses to locate the placeholder.
    /// </summary>
    public static readonly IReadOnlyList<CvTemplateSection> Sections =
    [
        new("CandidateHeader", "Candidate Header", "[Candidate name, headline, and contact line]"),
        new("ProfileSummary", "Professional Profile", "[Professional profile overview]"),
        new("KeySkills", "Key Technologies & Competencies", "[Comma-separated keyword line]"),
        new("Experience", "Professional Experience", "[Recent roles with title, company, period, and achievements]"),
        new("Projects", "Projects", "[Project entries with period, description, and links]"),
        new("Education", "Education", "[Education entries with degree, institution, and period]"),
        new("Certifications", "Certifications", "[Selected certifications]"),
        new("Languages", "Languages", "[Languages spoken with proficiency level]"),
        new("Recommendations", "Recommendations", "[Recommendation quotes with attribution]"),
        new("EarlyCareer", "Early Career", "[Condensed pre-cutoff roles and projects]"),
    ];

    /// <summary>
    /// Generates the CV Word template at <paramref name="outputPath"/>.
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
        AddFontTable(mainPart);

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
            // A4 = 210 x 297 mm = 11906 x 16838 twips (1 mm ≈ 56.6929 twips).
            new PageSize { Width = 11906, Height = 16838 },
            new PageMargin
            {
                // 2 cm margins (EU standard) = 1134 twips.
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
    }

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
                    // 22 half-points = 11 pt.
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

    /// <summary>
    /// Adds a minimal font table declaring Aptos with Calibri as its explicit
    /// alternate. Word uses this table to render a clean substitution when
    /// Aptos is absent on the viewing machine.
    /// </summary>
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

    /// <summary>
    /// Builds a <see cref="RunFonts"/> with Aptos as the primary ASCII/HighAnsi
    /// font and Calibri in the complex-script and east-Asian slots.
    /// </summary>
    private static RunFonts BuildRunFonts()
        => new()
        {
            Ascii = PrimaryFont,
            HighAnsi = PrimaryFont,
            ComplexScript = PrimaryFontFallback,
            EastAsia = PrimaryFontFallback
        };

    private static Style CreateBodyStyle()
    {
        return new Style(
            new StyleName { Val = "Normal" },
            new StyleRunProperties(
                BuildRunFonts(),
                new FontSize { Val = "22" },
                new Color { Val = "1F1F1F" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines
                {
                    After = "80",
                    Line = "276",
                    LineRule = LineSpacingRuleValues.Auto
                }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "Normal",
            Default = true
        };
    }

    /// <summary>
    /// Heading 1: 16 pt bold, accent-coloured text, thin bottom border in the
    /// same accent colour. Used for section titles (Profile, Experience, …).
    /// </summary>
    private static Style CreateHeading1Style()
    {
        return new Style(
            new StyleName { Val = "heading 1" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleRunProperties(
                BuildRunFonts(),
                new Bold { Val = OnOffValue.FromBoolean(true) },
                // 32 half-points = 16 pt.
                new FontSize { Val = "32" },
                new Color { Val = AccentColorHex }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "280", After = "120" },
                new ParagraphBorders(
                    new BottomBorder
                    {
                        Val = BorderValues.Single,
                        Size = 6,
                        Space = 2,
                        Color = AccentColorHex
                    })))
        {
            Type = StyleValues.Paragraph,
            StyleId = "Heading1"
        };
    }

    /// <summary>
    /// Heading 2: 14 pt bold dark-grey. Used for role titles and secondary
    /// groupings within a section.
    /// </summary>
    private static Style CreateHeading2Style()
    {
        return new Style(
            new StyleName { Val = "heading 2" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleRunProperties(
                BuildRunFonts(),
                new Bold { Val = OnOffValue.FromBoolean(true) },
                // 28 half-points = 14 pt.
                new FontSize { Val = "28" },
                new Color { Val = "1F1F1F" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "200", After = "60" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "Heading2"
        };
    }

    /// <summary>
    /// Heading 3: 12 pt bold dark-grey. Used for sub-roles or project titles.
    /// </summary>
    private static Style CreateHeading3Style()
    {
        return new Style(
            new StyleName { Val = "heading 3" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleRunProperties(
                BuildRunFonts(),
                new Bold { Val = OnOffValue.FromBoolean(true) },
                // 24 half-points = 12 pt.
                new FontSize { Val = "24" },
                new Color { Val = "2A2A2A" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "120", After = "40" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "Heading3"
        };
    }

    /// <summary>
    /// Contact line style: the header's one-line contact row
    /// (<c>city · email · phone · LinkedIn</c>). Slightly smaller than body,
    /// muted grey, minimal after-spacing.
    /// </summary>
    private static Style CreateContactLineStyle()
    {
        return new Style(
            new StyleName { Val = "Contact Line" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleRunProperties(
                BuildRunFonts(),
                // 20 half-points = 10 pt.
                new FontSize { Val = "20" },
                new Color { Val = "4A4A4A" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "0", After = "240" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "ContactLine"
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
