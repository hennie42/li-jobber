using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LiCvWriter.Infrastructure.Documents.Templates;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class CvWordTemplateGeneratorTests
{
    [Fact]
    public void Generate_ProducesTemplateWithAllTaggedSections()
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"licvwriter-template-{Guid.NewGuid():N}.dotx");

        try
        {
            CvWordTemplateGenerator.Generate(outputPath);

            Assert.True(File.Exists(outputPath));

            using var document = WordprocessingDocument.Open(outputPath, isEditable: false);

            Assert.Equal(WordprocessingDocumentType.Template, document.DocumentType);

            var mainPart = document.MainDocumentPart;
            Assert.NotNull(mainPart);
            var documentRoot = mainPart.Document;
            Assert.NotNull(documentRoot);
            var body = documentRoot.Body;
            Assert.NotNull(body);

            var tags = body.Descendants<SdtBlock>()
                .Select(static sdt => sdt.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value)
                .Where(static value => value is not null)
                .ToArray();

            foreach (var section in CvWordTemplateGenerator.Sections)
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
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }
}
