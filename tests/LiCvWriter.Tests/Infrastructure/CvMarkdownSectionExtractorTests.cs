using LiCvWriter.Infrastructure.Documents;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class CvMarkdownSectionExtractorTests
{
    private const string SampleCvMarkdown = """
# Alex Taylor

> Senior Architect

## Target Role

- Role: Lead Architect
- Company: Contoso

## Professional Profile

Experienced architect with deep cloud delivery experience.

**Key Technologies & Competencies:** Azure, .NET, Kubernetes

## Fit Snapshot

- Strength: Cloud architecture
- Strength: Team leadership

## Professional Experience

### Lead Architect | Contoso

2020-2024

Led the cloud migration program.

## Recommendations

**Pat Reviewer**, CTO

> Alex is exceptional.
""";

    [Fact]
    public void ExtractCandidateHeader_ReturnsNameHeadlineAndTargetRole()
    {
        var header = CvMarkdownSectionExtractor.ExtractCandidateHeader(SampleCvMarkdown);

        Assert.NotNull(header);
        Assert.Contains("# Alex Taylor", header);
        Assert.Contains("> Senior Architect", header);
        Assert.Contains("## Target Role", header);
        Assert.Contains("Lead Architect", header);
        Assert.DoesNotContain("Professional Profile", header);
    }

    [Fact]
    public void ExtractProfileSummary_ReturnsBodyWithoutKeywordLine()
    {
        var summary = CvMarkdownSectionExtractor.ExtractProfileSummary(SampleCvMarkdown);

        Assert.NotNull(summary);
        Assert.Contains("Experienced architect", summary);
        Assert.DoesNotContain("Key Technologies", summary);
    }

    [Fact]
    public void ExtractKeySkills_ReturnsCommaSeparatedKeywords()
    {
        var keySkills = CvMarkdownSectionExtractor.ExtractKeySkills(SampleCvMarkdown);

        Assert.Equal("Azure, .NET, Kubernetes", keySkills);
    }

    [Fact]
    public void ExtractSection_ReturnsBodyOfMatchingSection()
    {
        var fit = CvMarkdownSectionExtractor.ExtractSection(SampleCvMarkdown, "Fit Snapshot", "Matchvurdering");

        Assert.NotNull(fit);
        Assert.Contains("Cloud architecture", fit);
        Assert.Contains("Team leadership", fit);
        Assert.DoesNotContain("Professional Experience", fit);
    }

    [Fact]
    public void ExtractSection_ReturnsNullWhenHeadingMissing()
    {
        var missing = CvMarkdownSectionExtractor.ExtractSection(SampleCvMarkdown, "Certifications", "Certificeringer");

        Assert.Null(missing);
    }

    [Fact]
    public void ExtractSection_MatchesDanishHeading()
    {
        var danishMarkdown = """
## Matchvurdering

- Styrke: Skyarkitektur
""";

        var fit = CvMarkdownSectionExtractor.ExtractSection(danishMarkdown, "Fit Snapshot", "Matchvurdering");

        Assert.NotNull(fit);
        Assert.Contains("Skyarkitektur", fit);
    }

    [Fact]
    public void ExtractEducation_ReturnsBodyForEnglishHeading()
    {
        const string markdown = """
## Education

- **MSc Computer Science** | University of Aarhus (2008-2010)
- **BSc Software Engineering** | DTU (2005-2008)

## Certifications

- Azure Solutions Architect
""";

        var education = CvMarkdownSectionExtractor.ExtractEducation(markdown);

        Assert.NotNull(education);
        Assert.Contains("MSc Computer Science", education);
        Assert.Contains("DTU", education);
        Assert.DoesNotContain("Azure Solutions Architect", education);
    }

    [Fact]
    public void ExtractEducation_ReturnsBodyForDanishHeading()
    {
        const string markdown = """
## Uddannelse

- **Cand.scient. Datalogi** | Aarhus Universitet (2008-2010)
""";

        var education = CvMarkdownSectionExtractor.ExtractEducation(markdown);

        Assert.NotNull(education);
        Assert.Contains("Cand.scient. Datalogi", education);
    }

    [Fact]
    public void ExtractLanguages_ReturnsBodyForEnglishHeading()
    {
        const string markdown = """
## Languages

Danish — Native, English — Professional

## Recommendations

- Pat
""";

        var languages = CvMarkdownSectionExtractor.ExtractLanguages(markdown);

        Assert.NotNull(languages);
        Assert.Contains("Danish — Native", languages);
        Assert.Contains("English — Professional", languages);
        Assert.DoesNotContain("Pat", languages);
    }

    [Fact]
    public void ExtractLanguages_ReturnsBodyForDanishHeading()
    {
        const string markdown = """
## Sprog

Dansk — Modersmål, Engelsk — Professionel
""";

        var languages = CvMarkdownSectionExtractor.ExtractLanguages(markdown);

        Assert.NotNull(languages);
        Assert.Contains("Modersmål", languages);
        Assert.Contains("Professionel", languages);
    }

    [Fact]
    public void ExtractEducation_ReturnsNullWhenMissing()
    {
        const string markdown = """
## Professional Profile

Body.
""";

        Assert.Null(CvMarkdownSectionExtractor.ExtractEducation(markdown));
        Assert.Null(CvMarkdownSectionExtractor.ExtractLanguages(markdown));
    }
}
