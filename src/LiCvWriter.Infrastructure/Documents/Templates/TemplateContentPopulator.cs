using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HtmlToOpenXml;
using LiCvWriter.Infrastructure.Workflows;
using Markdig;

namespace LiCvWriter.Infrastructure.Documents.Templates;

/// <summary>
/// Populates and prunes named content controls (<see cref="SdtBlock"/>) inside
/// a Word document opened from a template. The control discriminator is the
/// <see cref="Tag"/> value set on the template's structured document tags.
/// </summary>
public static class TemplateContentPopulator
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>
    /// Replaces the content of the <see cref="SdtBlock"/> matching <paramref name="tag"/>
    /// with the rendered output of <paramref name="markdownContent"/>. Returns
    /// <see langword="true"/> when a control was found and populated, <see langword="false"/>
    /// when no control with the given tag exists or the content is blank.
    /// </summary>
    /// <param name="mainPart">The opened document's main part.</param>
    /// <param name="tag">The <see cref="Tag.Val"/> identifying the target control.</param>
    /// <param name="markdownContent">The Markdown fragment to render into the control.</param>
    public static bool PopulateContentControl(MainDocumentPart mainPart, string tag, string? markdownContent)
    {
        ArgumentNullException.ThrowIfNull(mainPart);
        ArgumentException.ThrowIfNullOrEmpty(tag);

        if (string.IsNullOrWhiteSpace(markdownContent))
        {
            return false;
        }

        var sdt = FindSdtBlockByTag(mainPart, tag);
        if (sdt is null)
        {
            return false;
        }

        var content = sdt.GetFirstChild<SdtContentBlock>();
        if (content is null)
        {
            content = new SdtContentBlock();
            sdt.AppendChild(content);
        }

        content.RemoveAllChildren();

        var normalized = LlmMarkdownNormalizer.Normalize(markdownContent);
        var html = Markdown.ToHtml(normalized.Trim(), MarkdownPipeline);
        var elements = ConvertHtmlToBlockElements(mainPart, html);

        if (elements.Count == 0)
        {
            // Always leave at least an empty paragraph so the document remains valid.
            content.AppendChild(new Paragraph());
            return true;
        }

        // Post-process: flatten hyperlinks into plain runs so ATS and print
        // consumers don't see spurious underlines from HtmlToOpenXml's
        // automatic link generation.
        foreach (var element in elements)
        {
            FlattenHyperlinks(element);
        }

        // Strip any remaining underline formatting that survived hyperlink
        // flattening (e.g. from <ins>, <u>, or emphasis-extra markdown
        // extensions). CV body text should never contain underlined fragments.
        foreach (var element in elements)
        {
            StripUnderlines(element);
        }

        foreach (var element in elements)
        {
            content.AppendChild(element);
        }

        return true;
    }

    /// <summary>
    /// Removes any <see cref="SdtBlock"/> from the body whose tag is not present
    /// in <paramref name="populatedTags"/>. Use after population to drop empty
    /// optional sections (e.g. recommendations or certifications when the
    /// candidate has none).
    /// </summary>
    /// <param name="body">The document body containing template controls.</param>
    /// <param name="populatedTags">Set of tags that were successfully populated.</param>
    public static int RemoveEmptyControls(Body body, IReadOnlySet<string> populatedTags)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(populatedTags);

        var emptyControls = body.Descendants<SdtBlock>()
            .Where(sdt =>
            {
                var tagValue = sdt.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value;
                return tagValue is not null && !populatedTags.Contains(tagValue);
            })
            .ToArray();

        foreach (var control in emptyControls)
        {
            control.Remove();
        }

        return emptyControls.Length;
    }

    /// <summary>
    /// Lifts every remaining <see cref="SdtBlock"/>'s content out of its
    /// structured-document-tag wrapper so the saved document contains plain
    /// paragraphs/lists/headings only. ATS parsers and LLM-based CV readers
    /// often skip or mishandle SDT-wrapped content; unwrapping makes the
    /// document portable for downstream consumers.
    /// </summary>
    /// <param name="body">The document body to unwrap.</param>
    /// <returns>The number of SDT wrappers removed.</returns>
    public static int UnwrapAllSdtBlocks(Body body)
    {
        ArgumentNullException.ThrowIfNull(body);

        // Materialize first because we mutate the tree as we iterate.
        var sdtBlocks = body.Descendants<SdtBlock>().ToArray();
        var removed = 0;

        foreach (var sdt in sdtBlocks)
        {
            var parent = sdt.Parent;
            if (parent is null)
            {
                continue;
            }

            var content = sdt.GetFirstChild<SdtContentBlock>();
            if (content is null)
            {
                sdt.Remove();
                removed++;
                continue;
            }

            var children = content.ChildElements.ToArray();
            foreach (var child in children)
            {
                child.Remove();
                parent.InsertBefore(child, sdt);
            }

            sdt.Remove();
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Returns the <see cref="SdtBlock"/> whose <see cref="Tag.Val"/> matches
    /// <paramref name="tag"/>, or <see langword="null"/> when no such control exists.
    /// </summary>
    public static SdtBlock? FindSdtBlockByTag(MainDocumentPart mainPart, string tag)
    {
        var body = mainPart.Document?.Body;
        if (body is null)
        {
            return null;
        }

        return body
            .Descendants<SdtBlock>()
            .FirstOrDefault(sdt => sdt.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value == tag);
    }

    /// <summary>
    /// Replaces <see cref="Hyperlink"/> elements inside <paramref name="element"/>
    /// with their child <see cref="Run"/>s so the text renders as normal body
    /// text without underline or link colour. HtmlToOpenXml automatically
    /// converts <c>&lt;a href&gt;</c> tags to hyperlinks, but CV body text
    /// should not contain clickable links or underlined fragments.
    /// </summary>
    private static void FlattenHyperlinks(OpenXmlElement element)
    {
        var hyperlinks = element.Descendants<Hyperlink>().ToArray();
        foreach (var hyperlink in hyperlinks)
        {
            var parent = hyperlink.Parent;
            if (parent is null)
            {
                continue;
            }

            var runs = hyperlink.Elements<Run>().ToArray();
            foreach (var run in runs)
            {
                // Strip underline and blue colour that HtmlToOpenXml adds for links.
                var rpr = run.RunProperties;
                rpr?.RemoveAllChildren<Underline>();
                rpr?.RemoveAllChildren<Color>();

                run.Remove();
                parent.InsertBefore(run, hyperlink);
            }

            hyperlink.Remove();
        }
    }

    /// <summary>
    /// Removes all <see cref="Underline"/> elements from runs inside
    /// <paramref name="element"/> so no text in the CV appears underlined.
    /// </summary>
    private static void StripUnderlines(OpenXmlElement element)
    {
        foreach (var underline in element.Descendants<Underline>().ToArray())
        {
            underline.Remove();
        }
    }

    /// <summary>
    /// Converts an HTML fragment into block-level OpenXml elements suitable for
    /// inserting into a content control. Uses <see cref="HtmlConverter"/> to
    /// parse the HTML, captures the elements it appends to the document body,
    /// then detaches them so the caller can place them where needed.
    /// </summary>
    private static List<OpenXmlElement> ConvertHtmlToBlockElements(MainDocumentPart mainPart, string html)
    {        var body = mainPart.Document?.Body
            ?? throw new InvalidOperationException("Document body is missing — cannot convert HTML.");
        var existingChildren = body.ChildElements.ToHashSet();

        var converter = new HtmlConverter(mainPart);
        converter.ParseBody(html);

        var newChildren = body.ChildElements
            .Where(child => !existingChildren.Contains(child))
            .ToList();

        foreach (var child in newChildren)
        {
            child.Remove();
        }

        return newChildren;
    }
}
