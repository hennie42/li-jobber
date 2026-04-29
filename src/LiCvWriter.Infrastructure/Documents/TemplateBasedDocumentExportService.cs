using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Documents;
using LiCvWriter.Infrastructure.Documents.Templates;

namespace LiCvWriter.Infrastructure.Documents;

/// <summary>
/// Exports generated documents to disk as Word (<c>.docx</c>) files. CVs are
/// produced from the embedded <c>cv-template.dotx</c> by populating named
/// content controls per section. Recommendations use their own template;
/// other document kinds use the focused application-material template.
/// </summary>
public sealed class TemplateBasedDocumentExportService(StorageOptions options) : IDocumentExportService
{
    /// <summary>
    /// Maps each tagged content control in <c>cv-template.dotx</c> to a markdown
    /// extractor that produces the section's content from the rendered CV
    /// markdown. Order matches the template's section ordering.
    /// </summary>
    private static readonly IReadOnlyList<CvSectionMapping> CvSectionMappings =
    [
        new("CandidateHeader", null, null, null, CvMarkdownSectionExtractor.ExtractCandidateHeader),
        new("ProfileSummary", CvSection.ProfileSummary, "Professional Profile", "Professionel profil", CvMarkdownSectionExtractor.ExtractProfileSummary),
        new("KeySkills", CvSection.KeySkills, "Key Technologies & Competencies", "Nøgleteknologier og kompetencer", CvMarkdownSectionExtractor.ExtractKeySkills),
        new("Experience", CvSection.ExperienceHighlights, "Professional Experience", "Erhvervserfaring", markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Professional Experience", "Erhvervserfaring")),
        new("Projects", CvSection.ProjectHighlights, "Projects", "Projekter", markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Projects", "Projekter")),
        // Education and Languages are deterministic (no LLM round-trip) and
        // therefore have GeneratedSection = null; their content is recovered
        // from the rendered markdown via the section extractor.
        new("Education", null, null, null, CvMarkdownSectionExtractor.ExtractEducation),
        new("Certifications", null, null, null, markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Certifications", "Certificeringer")),
        new("Languages", null, null, null, CvMarkdownSectionExtractor.ExtractLanguages),
        new("EarlyCareer", null, null, null, markdown => CvMarkdownSectionExtractor.ExtractSection(markdown, "Early Career", "Tidlig karriere")),
        // FitSnapshot intentionally omitted: it is internal assessment data
        // (strengths/gaps for the user) and must not appear in the document
        // sent to recruiters. Any FitSnapshot SDT left in older templates is
        // pruned by RemoveEmptyControls below.
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
        else if (document.Kind is DocumentKind.Recommendations)
        {
            await Task.Run(() => GenerateRecommendationsFromTemplate(document, wordPath), cancellationToken);
        }
        else
        {
            await Task.Run(() => GenerateApplicationMaterialFromTemplate(document, wordPath), cancellationToken);
        }

        return new DocumentExportResult(document.Kind, wordPath);
    }

    /// <summary>
    /// Clones the embedded CV template, splits the rendered CV markdown into
    /// sections, populates each tagged content control, and removes any
    /// controls left empty (e.g. early-career when absent).
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

        using (var wordDoc = WordprocessingDocument.Open(outputPath, isEditable: true))
        {
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
                if ((usedRawSection || mapping.Tag == "KeySkills")
                    && mapping.EnglishSectionHeading is not null
                    && !string.IsNullOrWhiteSpace(sectionMarkdown))
                {
                    sectionMarkdown = EnsureSectionHeading(
                        sectionMarkdown,
                        SelectLocalizedHeading(document.Markdown, mapping.EnglishSectionHeading, mapping.DanishSectionHeading));
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

            // Inject the public-safe candidate snapshot as a custom XML part so
            // ATS systems and LLM-based CV parsers can read structured data from
            // the document without re-parsing the rendered text.
            AtsCustomXmlEmitter.Attach(wordDoc, document.AtsSnapshot);

            mainPart.Document!.Save();
        }

        NormalizeDocumentPackageContentTypes(outputPath);
    }

    private static void GenerateRecommendationsFromTemplate(GeneratedDocument document, string outputPath)
    {
        using (var templateStream = EmbeddedTemplateProvider.OpenTemplate(EmbeddedTemplateProvider.RecommendationsTemplateResourceName))
        using (var destinationStream = File.Create(outputPath))
        {
            templateStream.CopyTo(destinationStream);
        }

        using (var package = WordprocessingDocument.Open(outputPath, isEditable: true))
        {
            package.ChangeDocumentType(WordprocessingDocumentType.Document);
        }

        using (var wordDoc = WordprocessingDocument.Open(outputPath, isEditable: true))
        {
            var mainPart = wordDoc.MainDocumentPart
                ?? throw new InvalidOperationException("Recommendations template is missing its main document part.");

            SetCoreProperties(wordDoc, document);

            var populatedTags = new HashSet<string>(StringComparer.Ordinal);
            PopulateApplicationMaterialTag(mainPart, populatedTags, "CandidateHeader", ExtractApplicationCandidateHeader(document.Markdown));
            PopulateApplicationMaterialTag(mainPart, populatedTags, "TargetRole", CvMarkdownSectionExtractor.ExtractSection(document.Markdown, "Target Role", "Målrolle"));
            PopulateApplicationMaterialTag(mainPart, populatedTags, "DocumentIntro", CvMarkdownSectionExtractor.ExtractSection(document.Markdown, "Recommendation Brief", "Anbefalingsresumé"));
            PopulateApplicationMaterialTag(mainPart, populatedTags, "RecommendationsBody", CvMarkdownSectionExtractor.ExtractSection(document.Markdown, "Recommendations", "Anbefalinger"));

            var body = mainPart.Document?.Body
                ?? throw new InvalidOperationException("Recommendations template body missing.");

            TemplateContentPopulator.RemoveEmptyControls(body, populatedTags);
            TemplateContentPopulator.UnwrapAllSdtBlocks(body);

            mainPart.Document!.Save();
        }

        NormalizeDocumentPackageContentTypes(outputPath);
    }

    private static void GenerateApplicationMaterialFromTemplate(GeneratedDocument document, string outputPath)
    {
        using (var templateStream = EmbeddedTemplateProvider.OpenTemplate(EmbeddedTemplateProvider.ApplicationMaterialTemplateResourceName))
        using (var destinationStream = File.Create(outputPath))
        {
            templateStream.CopyTo(destinationStream);
        }

        using (var package = WordprocessingDocument.Open(outputPath, isEditable: true))
        {
            package.ChangeDocumentType(WordprocessingDocumentType.Document);
        }

        using (var wordDoc = WordprocessingDocument.Open(outputPath, isEditable: true))
        {
            var mainPart = wordDoc.MainDocumentPart
                ?? throw new InvalidOperationException("Application material template is missing its main document part.");

            SetCoreProperties(wordDoc, document);

            var populatedTags = new HashSet<string>(StringComparer.Ordinal);
            PopulateApplicationMaterialTag(mainPart, populatedTags, "CandidateHeader", ExtractApplicationCandidateHeader(document.Markdown));
            PopulateApplicationMaterialTag(mainPart, populatedTags, "TargetRole", CvMarkdownSectionExtractor.ExtractSection(document.Markdown, "Target Role", "Målrolle"));
            PopulateApplicationMaterialTag(mainPart, populatedTags, "DocumentBody", ExtractApplicationDocumentBody(document.Markdown));

            var body = mainPart.Document?.Body
                ?? throw new InvalidOperationException("Application material template body missing.");

            TemplateContentPopulator.RemoveEmptyControls(body, populatedTags);
            TemplateContentPopulator.UnwrapAllSdtBlocks(body);

            mainPart.Document!.Save();
        }

        NormalizeDocumentPackageContentTypes(outputPath);
    }

    private static void PopulateApplicationMaterialTag(
        MainDocumentPart mainPart,
        HashSet<string> populatedTags,
        string tag,
        string? markdown)
    {
        if (TemplateContentPopulator.PopulateContentControl(mainPart, tag, markdown))
        {
            populatedTags.Add(tag);
        }
    }

    private static string? ExtractApplicationCandidateHeader(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var lines = SplitMarkdownLines(markdown);
        var headerLines = lines.TakeWhile(static line => !line.StartsWith("## ", StringComparison.Ordinal)).ToArray();
        var result = string.Join(Environment.NewLine, headerLines).Trim();

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string? ExtractApplicationDocumentBody(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var lines = SplitMarkdownLines(markdown);
        var startIndex = -1;
        var passedTargetRole = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!line.StartsWith("## ", StringComparison.Ordinal))
            {
                continue;
            }

            var heading = line[3..].Trim();
            if (heading.Equals("Target Role", StringComparison.OrdinalIgnoreCase)
                || heading.Equals("Målrolle", StringComparison.OrdinalIgnoreCase))
            {
                passedTargetRole = true;
                continue;
            }

            if (passedTargetRole)
            {
                startIndex = index;
                break;
            }
        }

        if (startIndex < 0)
        {
            return null;
        }

        var result = string.Join(Environment.NewLine, lines[startIndex..]).Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string[] SplitMarkdownLines(string markdown)
        => markdown.Split(["\r\n", "\n"], StringSplitOptions.None);

    private static void SetCoreProperties(WordprocessingDocument document, GeneratedDocument generated)
    {
        var properties = document.CoreFilePropertiesPart
            ?? document.AddCoreFilePropertiesPart();

        // Keywords slot is the primary ATS-readable metadata field. Pack it
        // with the role title and the must-have themes so a keyword search
        // for the target role / required technologies hits the document
        // metadata even if the parser fails on the body content.
        var snapshot = generated.AtsSnapshot;
        var keywordParts = new List<string>(8) { generated.Title, generated.Kind.ToString() };
        if (snapshot is not null)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.TargetRoleTitle))
            {
                keywordParts.Add(snapshot.TargetRoleTitle);
            }

            keywordParts.AddRange(snapshot.MustHaveThemes.Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        var keywords = string.Join(", ", keywordParts
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase));

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

    private static string SelectLocalizedHeading(string documentMarkdown, string englishHeading, string? danishHeading)
    {
        if (!string.IsNullOrWhiteSpace(danishHeading)
            && documentMarkdown.Contains($"## {danishHeading}", StringComparison.OrdinalIgnoreCase))
        {
            return danishHeading;
        }

        return englishHeading;
    }

    private static string EnsureSectionHeading(string markdown, string heading)
    {
        var trimmed = markdown.Trim();
        if (trimmed.StartsWith($"## {heading}", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"## {heading}\n\n{trimmed}";
    }

    private static void NormalizeDocumentPackageContentTypes(string outputPath)
    {
        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Update);
        var contentTypesEntry = archive.GetEntry("[Content_Types].xml")
            ?? throw new InvalidOperationException("Generated Word package is missing [Content_Types].xml.");

        XDocument contentTypes;
        using (var stream = contentTypesEntry.Open())
        {
            contentTypes = XDocument.Load(stream);
        }

        XNamespace packageNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
        var staleTemplateDefaults = contentTypes.Root?
            .Elements(packageNamespace + "Default")
            .Where(element =>
                string.Equals((string?)element.Attribute("Extension"), "xml", StringComparison.OrdinalIgnoreCase)
                && string.Equals((string?)element.Attribute("ContentType"),
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml",
                    StringComparison.Ordinal))
            .ToArray() ?? [];

        if (staleTemplateDefaults.Length == 0)
        {
            return;
        }

        foreach (var element in staleTemplateDefaults)
        {
            element.Remove();
        }

        contentTypesEntry.Delete();
        var replacementEntry = archive.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal);
        using var replacementStream = replacementEntry.Open();
        contentTypes.Save(replacementStream, SaveOptions.DisableFormatting);
    }

    private sealed record CvSectionMapping(
        string Tag,
        CvSection? GeneratedSection,
        string? EnglishSectionHeading,
        string? DanishSectionHeading,
        Func<string, string?> Extract);
}
