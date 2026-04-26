using LiCvWriter.Core.Documents;
using LiCvWriter.Infrastructure.Documents;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class ApplicationMaterialLengthPolicyTests
{
    [Fact]
    public void Enforce_CvDocument_ReturnsDocumentUnchanged()
    {
        var document = new GeneratedDocument(
            DocumentKind.Cv,
            "CV",
            string.Join(" ", Enumerable.Repeat("word", 800)),
            "plain",
            DateTimeOffset.UtcNow);

        var result = ApplicationMaterialLengthPolicy.Enforce(document);

        Assert.Same(document, result);
    }

    [Fact]
    public void Enforce_CoverLetterOverBudget_TrimsToOnePageBudget()
    {
        var markdown = "# Alex Taylor\n\n## Cover Letter\n\n" + string.Join(" ", Enumerable.Repeat("evidence", 520));
        var document = new GeneratedDocument(
            DocumentKind.CoverLetter,
            "Cover Letter",
            markdown,
            markdown,
            DateTimeOffset.UtcNow);

        var result = ApplicationMaterialLengthPolicy.Enforce(document);

        var maxWordCount = ApplicationMaterialLengthPolicy.GetMaxWordCount(DocumentKind.CoverLetter);

        Assert.NotNull(maxWordCount);
        Assert.True(ApplicationMaterialLengthPolicy.CountWords(result.Markdown) <= maxWordCount.Value);
        Assert.Equal(result.Markdown, result.PlainText);
    }
}