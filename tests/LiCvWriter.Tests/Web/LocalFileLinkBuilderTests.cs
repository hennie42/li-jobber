using LiCvWriter.Web.Services;

namespace LiCvWriter.Tests.Web;

public sealed class LocalFileLinkBuilderTests
{
    [Fact]
    public void BuildFileUri_WindowsPath_ReturnsFileUri()
    {
        var uri = LocalFileLinkBuilder.BuildFileUri(@"C:\Exports\Job One\cover letter.docx");

        Assert.Equal("file:///C:/Exports/Job%20One/cover%20letter.docx", uri);
    }

    [Fact]
    public void BuildFolderUri_WindowsPath_ReturnsContainingFolderFileUri()
    {
        var uri = LocalFileLinkBuilder.BuildFolderUri(@"C:\Exports\Job One\cover letter.docx");

        Assert.Equal("file:///C:/Exports/Job%20One", uri);
    }

    [Fact]
    public void BuildFileUri_EmptyPath_ReturnsHashFallback()
    {
        var uri = LocalFileLinkBuilder.BuildFileUri(string.Empty);

        Assert.Equal("#", uri);
    }
}