using LiCvWriter.Infrastructure.Workflows;
using Markdig;

namespace LiCvWriter.Tests.Infrastructure;

public class LlmMarkdownNormalizerTests
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    [Fact]
    public void Normalize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, InvokeNormalize(null));
        Assert.Equal(string.Empty, InvokeNormalize(""));
        Assert.Equal("   ", InvokeNormalize("   "));
    }

    [Fact]
    public void Normalize_SplitsBulletsGluedAfterPunctuation()
    {
        const string input = "Senior Architect | Northwind Health.*Architected automation pipelines.*Drove API-first adoption.";

        var normalized = InvokeNormalize(input);

        Assert.Contains("\n- Architected automation pipelines", normalized);
        Assert.Contains("\n- Drove API-first adoption", normalized);
        Assert.DoesNotContain(".*", normalized);
    }

    [Fact]
    public void Normalize_SplitsBulletsGluedAfterWord()
    {
        // LLM omits period before bullet: "Present*Architected"
        const string input = "### Senior Architect | Northwind Health\nOct2025 - Present*Architected pipelines*Drove adoption.";

        var normalized = InvokeNormalize(input);

        Assert.Contains("- Architected pipelines", normalized);
        Assert.Contains("- Drove adoption", normalized);
    }

    [Fact]
    public void Normalize_RendersAsBulletListThroughMarkdig()
    {
        const string input = "### Senior Architect | Northwind Health\n2018-2024.*Architected pipelines.*Drove adoption.";

        var html = Markdown.ToHtml(InvokeNormalize(input), Pipeline);

        Assert.Contains("<h3", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("<li>Architected pipelines.</li>", html);
        Assert.Contains("<li>Drove adoption.</li>", html);
    }

    [Fact]
    public void Normalize_StandardizesAsteriskBulletsToHyphen()
    {
        const string input = "* one\n* two\n* three";

        var normalized = InvokeNormalize(input);

        Assert.Contains("- one", normalized);
        Assert.Contains("- two", normalized);
        Assert.Contains("- three", normalized);
        Assert.DoesNotContain("* one", normalized);
    }

    [Fact]
    public void Normalize_EnsuresBlankLineBeforeHeadingsAndLists()
    {
        const string input = "Some text.\n### Heading\nMore text.\n- bullet";

        var normalized = InvokeNormalize(input);

        Assert.Contains("Some text.\n\n### Heading", normalized);
        Assert.Contains("More text.\n\n- bullet", normalized);
    }

    [Fact]
    public void Normalize_StripsTrailingPeriodOnHeading()
    {
        const string input = "### Senior Architect | Northwind Health.";

        var normalized = InvokeNormalize(input);

        Assert.Contains("### Senior Architect | Northwind Health", normalized);
        Assert.DoesNotContain("Health.", normalized);
    }

    [Fact]
    public void Normalize_PreservesExistingWellFormedMarkdown()
    {
        const string input = "### Title\n\n- one\n- two\n";

        var normalized = InvokeNormalize(input);

        Assert.Contains("### Title", normalized);
        Assert.Contains("- one", normalized);
        Assert.Contains("- two", normalized);
        Assert.DoesNotContain("- -", normalized);
    }

    [Fact]
    public void Normalize_DoesNotMangleEmphasisRuns()
    {
        const string input = "This is **bold** and *italic* text.";

        var normalized = InvokeNormalize(input);

        Assert.Contains("**bold**", normalized);
        Assert.Contains("*italic*", normalized);
    }

    [Fact]
    public void Normalize_CollapsesExcessiveBlankLines()
    {
        const string input = "Line one\n\n\n\n\nLine two";

        var normalized = InvokeNormalize(input);

        Assert.DoesNotContain("\n\n\n", normalized);
        Assert.Contains("Line one\n\nLine two", normalized);
    }

    [Fact]
    public void Normalize_HandlesUnicodeBulletGlyph()
    {
        const string input = "intro.•First.•Second";

        var normalized = InvokeNormalize(input);

        Assert.Contains("- First", normalized);
        Assert.Contains("- Second", normalized);
    }

    [Fact]
    public void Normalize_SeparatesDateGluedToCompanyName()
    {
        const string input = "### Senior Architect | Northwind HealthOct2025 - Present";

        var normalized = InvokeNormalize(input);

        Assert.Contains("Northwind Health", normalized);
        Assert.Contains("Oct2025", normalized);
        Assert.DoesNotContain("HealthOct", normalized);
    }

    [Fact]
    public void Normalize_HandlesRealisticLlmExperienceOutput()
    {
        // Realistic LLM output: everything glued together, no line breaks.
        const string input =
            "### Senior Automation Architect | Northwind HealthOct2025 - Present*Architected automation pipelines.*Drove API-first adoption across 12 teams. " +
            "### Senior Cloud Architect | Northwind HealthApr2022 - Oct2025*Provided strategic architectural guidance.*Spearheaded containerization using Docker and Kubernetes.";

        var normalized = InvokeNormalize(input);

        // Headings should be on their own lines.
        Assert.Contains("### Senior Automation Architect | Northwind Health", normalized);
        Assert.Contains("### Senior Cloud Architect | Northwind Health", normalized);

        // Dates should be separated from company names.
        Assert.DoesNotContain("HealthOct", normalized);
        Assert.DoesNotContain("HealthApr", normalized);

        // Bullets should be proper list items.
        Assert.Contains("- Architected automation pipelines", normalized);
        Assert.Contains("- Drove API-first adoption", normalized);
        Assert.Contains("- Provided strategic architectural guidance", normalized);
        Assert.Contains("- Spearheaded containerization", normalized);

        // Markdig should produce a real list.
        var html = Markdown.ToHtml(normalized, Pipeline);
        Assert.Contains("<h3", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("<li>", html);
    }

    [Fact]
    public void Normalize_HandlesDashBulletsGluedAfterPunctuation()
    {
        const string input = "Oct2025 - Present.- Architected pipelines.- Drove adoption.";

        var normalized = InvokeNormalize(input);

        Assert.Contains("- Architected pipelines", normalized);
        Assert.Contains("- Drove adoption", normalized);
    }

    [Fact]
    public void Normalize_PromotesInlineRoleHeaderToHeading()
    {
        // The LLM glues the second role's header into the previous bullet
        // without ### markers — only a "Title | Company" pattern signals
        // a new role.
        const string input =
            "### Senior Automation Architect | Northwind Health\nOct 2025 - Present\n\n" +
            "- Coaching cross-functional teams on AI use. Senior Cloud Architect and Advisor | Northwind Health Apr 2022 - Oct 2025. Providing strategic architectural guidance.";

        var normalized = InvokeNormalize(input);

        Assert.Contains("### Senior Automation Architect | Northwind Health", normalized);
        Assert.Contains("### Senior Cloud Architect and Advisor | Northwind Health", normalized);
    }

    [Theory]
    [InlineData("C# 13, .NET 9, ASP.NET Core")]
    [InlineData("F# and C# interop")]
    [InlineData("J# legacy maintenance")]
    [InlineData("X#, Y#, Z# variants")]
    public void Normalize_PreservesLanguageNamesContainingHash(string input)
    {
        // Regression: the InlineHeading regex must not split tokens like "C#"
        // because they are language names, not glued markdown headings.
        var normalized = InvokeNormalize(input);

        Assert.Contains(input, normalized);
        // Must not have promoted the '#' to a heading on a new line.
        Assert.DoesNotContain("\n# ", normalized);
    }

    [Fact]
    public void Normalize_KeySkillsLineWithCSharpRendersAsParagraphNotHeading()
    {
        // Regression for visual-preview bug: the KeySkills line containing "C#"
        // was being split so that "# 13, .NET 9, …" became an H1.
        const string input = "## Key Technologies & Competencies\n\nC# 13, .NET 9, ASP.NET Core, Azure, Kubernetes";

        var normalized = InvokeNormalize(input);
        var html = Markdown.ToHtml(normalized, Pipeline);

        Assert.Contains("<h2", html);
        Assert.Contains("C# 13, .NET 9", html);
        // Body content must not be an H1.
        Assert.DoesNotContain("<h1", html);
    }

    [Fact]
    public void Normalize_SplitsHeadingFromGluedDescriptionAfterPresent()
    {
        // Regression for a real export defect: the LLM produced the heading
        // and lead paragraph on a single line as
        //   "### Title | Company | Oct2025 - Present- Description sentence...".
        // The description must drop into a paragraph BELOW the heading, not
        // become a bullet, and not bleed into the heading style.
        const string input =
            "### Senior Automation Architect | Northwind Health | Oct2025 - Present- Advance automation and integration projects by leveraging Agentic AI.\n\n" +
            "- Design and implement robust, API-first solutions.";

        var normalized = InvokeNormalize(input);
        var html = Markdown.ToHtml(normalized, Pipeline);

        // The heading line must end at "Present" and not contain the lead sentence.
        Assert.Contains("### Senior Automation Architect | Northwind Health | Oct2025 - Present", normalized);
        Assert.DoesNotContain("Present- Advance", normalized);
        // The lead sentence must render as a paragraph (not a list item, not part of <h3>).
        Assert.Contains("<h3", html);
        Assert.DoesNotContain("Advance automation and integration projects", ExtractFirstHeading(html));
        Assert.Contains("<p>Advance automation and integration projects", html);
        // The trailing "Design and implement..." must still be a list item.
        Assert.Contains("<li>Design and implement", html);
    }

    [Fact]
    public void Normalize_SplitsHeadingFromGluedDescriptionAfterYear()
    {
        // Variant of the above where the period ends with a year, not "Present":
        //   "### Title | Company | Aug2020 - Apr2022- Delivered ...".
        const string input =
            "### Principal Consultant | Fabrikam Advisory | Aug2020 - Apr2022- Delivered comprehensive IT consulting services across the Nordic countries.";

        var normalized = InvokeNormalize(input);
        var html = Markdown.ToHtml(normalized, Pipeline);

        Assert.Contains("### Principal Consultant | Fabrikam Advisory | Aug2020 - Apr2022", normalized);
        Assert.DoesNotContain("Apr2022- Delivered", normalized);
        Assert.DoesNotContain("Delivered comprehensive IT", ExtractFirstHeading(html));
        Assert.Contains("<p>Delivered comprehensive IT", html);
    }

    [Fact]
    public void Normalize_PreservesYearRangesLikeTwentyTwentyDashTwentyTwentyFour()
    {
        // Date ranges "2020-2024" must NOT be split: there is no whitespace
        // after the dash, so the new paragraph rule must skip them.
        const string input = "### Senior Architect | Contoso | 2020-2024";

        var normalized = InvokeNormalize(input);

        Assert.Contains("2020-2024", normalized);
        Assert.DoesNotContain("2020\n", normalized);
        Assert.DoesNotContain("2024\n\n", normalized);
    }

    private static string ExtractFirstHeading(string html)
    {
        var start = html.IndexOf("<h3", StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        var end = html.IndexOf("</h3>", start, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..(end + 5)];
    }

    private static string InvokeNormalize(string? input)
        => LlmMarkdownNormalizer.Normalize(input);
}
