using LiCvWriter.Web.SharedUI.Markdown;

namespace LiCvWriter.Tests.Web;

public sealed class ClientMarkdownRendererTests
{
    [Fact]
    public void RenderHtml_WhenMarkdownContainsHeadingAndBullets_RendersPreviewBlocks()
    {
        var markdown = """
        # Candidate Name

        ## Professional Profile
        Cloud architect with **Azure** experience.

        - Led platform delivery
        - Improved `developer experience`
        """;

        var html = ClientMarkdownRenderer.RenderHtml(markdown);

        Assert.Contains("<h1>Candidate Name</h1>", html);
        Assert.Contains("<h2>Professional Profile</h2>", html);
        Assert.Contains("<strong>Azure</strong>", html);
        Assert.Contains("<li>Led platform delivery</li>", html);
        Assert.Contains("<code>developer experience</code>", html);
    }

    [Fact]
    public void RenderHtml_WhenMarkdownContainsHtml_EncodesUnsafeContent()
    {
        const string markdown = "# Title\n<script>alert('x')</script>\n- <b>not trusted</b>";

        var html = ClientMarkdownRenderer.RenderHtml(markdown);

        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;", html);
        Assert.Contains("&lt;b&gt;not trusted&lt;/b&gt;", html);
    }
}
