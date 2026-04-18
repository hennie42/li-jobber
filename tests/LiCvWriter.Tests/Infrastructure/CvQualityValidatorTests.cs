using LiCvWriter.Application.Models;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Infrastructure.Documents;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class CvQualityValidatorTests
{
    [Fact]
    public void ValidateAndAutoFix_Cv_ReordersProjectsBeforeExperience_WhenProjectsCoverMoreThemes()
    {
        var validator = new CvQualityValidator();
        var request = BuildRequest(mustHaveThemes: ["Kubernetes", "Terraform"]);
        var document = BuildCvDocument(
            """
# Alex Taylor

## Professional Profile

Strong profile line 1.
Strong profile line 2.

## Professional Experience

- Led distributed delivery programs.

## Projects

- Built Kubernetes platform for regulated workloads.
- Automated Terraform environment promotion.

## Recommendations

- Great collaborator.
""");

        var result = validator.ValidateAndAutoFix(document, request);

        Assert.True(result.Report.SectionOrderChanged);
        Assert.Contains("ReorderedSectionsForKeywordCoverage", result.Report.AppliedFixes);

        var projectsIndex = result.Document.Markdown.IndexOf("## Projects", StringComparison.Ordinal);
        var experienceIndex = result.Document.Markdown.IndexOf("## Professional Experience", StringComparison.Ordinal);
        Assert.True(projectsIndex >= 0);
        Assert.True(experienceIndex >= 0);
        Assert.True(projectsIndex < experienceIndex);
    }

    [Fact]
    public void ValidateAndAutoFix_Cv_TrimsOptionalSections_WhenLengthExceedsOnePageHeuristic()
    {
        var validator = new CvQualityValidator();
        var request = BuildRequest();

        var longRecommendations = string.Join(Environment.NewLine, Enumerable.Range(1, 90).Select(index => $"- Recommendation line {index}"));
        var longCertifications = string.Join(Environment.NewLine, Enumerable.Range(1, 70).Select(index => $"- Certification line {index}"));

        var document = BuildCvDocument(
            $"""
# Alex Taylor

## Professional Profile

line 1
line 2
line 3
line 4
line 5

## Professional Experience

- Delivered 3 complex transformation programs.

## Projects

- Built delivery platform.

## Recommendations

{longRecommendations}

## Certifications

{longCertifications}
""");

        var result = validator.ValidateAndAutoFix(document, request);

        Assert.Contains("TrimmedOptionalSectionsForLength", result.Report.AppliedFixes);
        Assert.DoesNotContain("## Recommendations", result.Document.Markdown);
        Assert.DoesNotContain("## Certifications", result.Document.Markdown);
        Assert.Contains("Recommendations", result.Report.TrimmedOptionalSections);
        Assert.Contains("Certifications", result.Report.TrimmedOptionalSections);
    }

    [Fact]
    public void ValidateAndAutoFix_NonCv_ReturnsUnchanged()
    {
        var validator = new CvQualityValidator();
        var request = BuildRequest();
        var document = new GeneratedDocument(
            DocumentKind.CoverLetter,
            "Cover Letter",
            "## Letter\n\nText",
            "Text",
            DateTimeOffset.UtcNow);

        var result = validator.ValidateAndAutoFix(document, request);

        Assert.Equal(document, result.Document);
        Assert.Equal(CvQualityReport.Empty, result.Report);
    }

    private static DraftGenerationRequest BuildRequest(IReadOnlyList<string>? mustHaveThemes = null)
        => new(
            new CandidateProfile { Name = new PersonName("Alex", "Taylor") },
            new JobPostingAnalysis
            {
                RoleTitle = "Lead Architect",
                CompanyName = "Contoso",
                Summary = "Summary",
                MustHaveThemes = mustHaveThemes ?? Array.Empty<string>(),
                NiceToHaveThemes = Array.Empty<string>()
            },
            CompanyContext: "Context",
            AdditionalInstructions: "",
            DocumentKinds: [DocumentKind.Cv],
            ExportToFiles: false);

    private static GeneratedDocument BuildCvDocument(string markdown)
        => new(
            DocumentKind.Cv,
            "CV",
            markdown,
            markdown,
            DateTimeOffset.UtcNow);
}
