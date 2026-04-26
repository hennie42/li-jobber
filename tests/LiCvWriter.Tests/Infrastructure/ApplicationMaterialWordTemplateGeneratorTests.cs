using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using LiCvWriter.Infrastructure.Documents.Templates;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class ApplicationMaterialWordTemplateGeneratorTests
{
    [Fact]
    public void RegenerateEmbeddedApplicationMaterialTemplate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LiCvWriter.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var templatePath = Path.Combine(dir.FullName,
            "src", "LiCvWriter.Infrastructure", "Documents", "Templates", "application-material-template.dotx");

        ApplicationMaterialWordTemplateGenerator.Generate(templatePath);
        Assert.True(File.Exists(templatePath));
    }

    [Fact]
    public void Generate_ProducesTemplateWithAllTaggedSections()
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"licvwriter-application-template-{Guid.NewGuid():N}.dotx");

        try
        {
            ApplicationMaterialWordTemplateGenerator.Generate(outputPath);

            Assert.True(File.Exists(outputPath));

            using var document = WordprocessingDocument.Open(outputPath, isEditable: false);
            Assert.Equal(WordprocessingDocumentType.Template, document.DocumentType);

            var mainPart = document.MainDocumentPart;
            Assert.NotNull(mainPart);

            var body = mainPart.Document?.Body;
            Assert.NotNull(body);

            var tags = body.Descendants<SdtBlock>()
                .Select(static sdt => sdt.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value)
                .Where(static value => value is not null)
                .ToArray();

            foreach (var section in ApplicationMaterialWordTemplateGenerator.Sections)
            {
                Assert.Contains(section.Tag, tags);
            }

            var styles = mainPart.StyleDefinitionsPart?.Styles;
            Assert.NotNull(styles);
            var styleIds = styles.Elements<Style>()
                .Select(static style => style.StyleId?.Value)
                .Where(static value => value is not null)
                .ToArray();

            Assert.Contains("Normal", styleIds);
            Assert.Contains("Heading1", styleIds);
            Assert.Contains("Heading2", styleIds);
            Assert.Contains("Heading3", styleIds);

            var validationErrors = new OpenXmlValidator().Validate(document).ToArray();
            Assert.True(validationErrors.Length == 0,
                "Application material template OpenXML validation errors:\n" + string.Join("\n", validationErrors.Select(static error =>
                    $"- {error.Part?.Uri}: {error.Path?.XPath}: {error.Description}")));
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public void Generate_UsesStableContentControlIds()
    {
        var firstPath = Path.Combine(
            Path.GetTempPath(),
            $"licvwriter-application-template-{Guid.NewGuid():N}.dotx");
        var secondPath = Path.Combine(
            Path.GetTempPath(),
            $"licvwriter-application-template-{Guid.NewGuid():N}.dotx");

        try
        {
            ApplicationMaterialWordTemplateGenerator.Generate(firstPath);
            ApplicationMaterialWordTemplateGenerator.Generate(secondPath);

            using var firstDocument = WordprocessingDocument.Open(firstPath, isEditable: false);
            using var secondDocument = WordprocessingDocument.Open(secondPath, isEditable: false);

            var firstIds = ReadContentControlIds(firstDocument);
            var secondIds = ReadContentControlIds(secondDocument);

            Assert.Equal(firstIds, secondIds);
        }
        finally
        {
            if (File.Exists(firstPath))
            {
                File.Delete(firstPath);
            }

            if (File.Exists(secondPath))
            {
                File.Delete(secondPath);
            }
        }
    }

    private static IReadOnlyDictionary<string, int> ReadContentControlIds(WordprocessingDocument document)
        => document.MainDocumentPart!.Document!.Body!.Descendants<SdtBlock>()
            .Select(static sdt => new
            {
                Tag = sdt.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value,
                Id = sdt.SdtProperties?.GetFirstChild<SdtId>()?.Val?.Value
            })
            .Where(static item => item.Tag is not null && item.Id is not null)
            .ToDictionary(static item => item.Tag!, static item => item.Id!.Value, StringComparer.Ordinal);
}