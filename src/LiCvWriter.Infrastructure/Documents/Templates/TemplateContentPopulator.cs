using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HtmlToOpenXml;
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

        var html = Markdown.ToHtml(markdownContent.Trim(), MarkdownPipeline);
        var elements = ConvertHtmlToBlockElements(mainPart, html);

        if (elements.Count == 0)
        {
            // Always leave at least an empty paragraph so the document remains valid.
            content.AppendChild(new Paragraph());
            return true;
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
    /// Converts an HTML fragment into block-level OpenXml elements suitable for
    /// inserting into a content control. Uses <see cref="HtmlConverter"/> to
    /// parse the HTML, captures the elements it appends to the document body,
    /// then detaches them so the caller can place them where needed.
    /// </summary>
    private static List<OpenXmlElement> ConvertHtmlToBlockElements(MainDocumentPart mainPart, string html)
    {
        var body = mainPart.Document?.Body
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
