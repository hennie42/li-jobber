using LiCvWriter.Application.Models;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Infrastructure.Documents;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class MarkdownDocumentRendererTests
{
    [Fact]
    public async Task RenderAsync_Cv_IncludesProfileOverviewSection()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest(generatedBody: "Cloud-native architect with deep Azure experience.");

        var result = await renderer.RenderAsync(request);

        Assert.Contains("## Professional Profile", result.Markdown);
        Assert.Contains("Cloud-native architect with deep Azure experience.", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_IncludesExperienceSection()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest();

        var result = await renderer.RenderAsync(request);

        Assert.Contains("## Professional Experience", result.Markdown);
        Assert.Contains("### Lead Architect | Contoso", result.Markdown);
        Assert.Contains("### Senior Developer | Fabrikam", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_IncludesProjectsSection()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest();

        var result = await renderer.RenderAsync(request);

        Assert.Contains("## Projects", result.Markdown);
        Assert.Contains("**Cloud Migration Portal**", result.Markdown);
        Assert.Contains("Built a self-service migration portal.", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_IncludesAllRecommendations()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest();

        var result = await renderer.RenderAsync(request);

        Assert.Contains("## Recommendations", result.Markdown);
        Assert.Contains("> *\"", result.Markdown);
        Assert.Contains("Jane Smith", result.Markdown);
        Assert.Contains("Lars Madsen", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_AnnotatesDanishRecommendationForEnglishOutput()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest(outputLanguage: OutputLanguage.English);

        var result = await renderer.RenderAsync(request);

        Assert.Contains("(translated from Danish)", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_AnnotatesEnglishRecommendationForDanishOutput()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest(outputLanguage: OutputLanguage.Danish);

        var result = await renderer.RenderAsync(request);

        Assert.Contains("(translated from English)", result.Markdown);
    }

    [Fact]
    public void DetectDanish_ReturnsTrueForDanishText()
    {
        var danishText = "Han har altid været en dygtig og pålidelig kollega som er med til at løfte teamet og har stor erfaring med softwareudvikling.";

        Assert.True(MarkdownDocumentRenderer.DetectDanish(danishText));
    }

    [Fact]
    public void DetectDanish_ReturnsFalseForEnglishText()
    {
        var englishText = "An exceptional architect who consistently delivers impactful solutions with strong technical leadership and clear communication.";

        Assert.False(MarkdownDocumentRenderer.DetectDanish(englishText));
    }

    [Fact]
    public void DetectDanish_ReturnsFalseForShortText()
    {
        Assert.False(MarkdownDocumentRenderer.DetectDanish("Hej"));
    }

    [Fact]
    public void GetTranslationAnnotation_ReturnsEmptyWhenLanguagesMatch()
    {
        var annotation = MarkdownDocumentRenderer.GetTranslationAnnotation(
            "An exceptional architect who delivers great results.", OutputLanguage.English);

        Assert.Equal(string.Empty, annotation);
    }

    [Fact]
    public async Task RenderAsync_Cv_IncludesKeywordLine()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest(
            mustHaveThemes: ["Azure", "Kubernetes"],
            evidenceTags: ["Azure", "Kubernetes", "Docker"]);

        var result = await renderer.RenderAsync(request);

        Assert.Contains("Key Technologies & Competencies", result.Markdown);
        Assert.Contains("Azure", result.Markdown);
        Assert.Contains("Kubernetes", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_GroupsPre2008ExperienceAndProjectsUnderEarlyCareer()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest();
        var candidate = request.Candidate with
        {
            Experience =
            [
                new ExperienceEntry("LegacyCorp", "Junior Engineer", "Maintained legacy systems.", "Aarhus", new DateRange(new PartialDate("2004", 2004), new PartialDate("2007", 2007))),
                new ExperienceEntry("Contoso", "Lead Architect", "Led cloud migration.", "Seattle", new DateRange(new PartialDate("2019", 2019), null))
            ],
            Projects =
            [
                new ProjectEntry("Legacy Platform Upgrade", "Delivered upgrade in older estate.", null, new DateRange(new PartialDate("2006", 2006), new PartialDate("2007", 2007))),
                new ProjectEntry("Cloud Migration Portal", "Built a self-service migration portal.", null, new DateRange(new PartialDate("2021", 2021), null))
            ]
        };

        var datedRequest = request with { Candidate = candidate };

        var result = await renderer.RenderAsync(datedRequest);

        Assert.Contains("## Early Career", result.Markdown);
        Assert.Contains("**Junior Engineer** | LegacyCorp", result.Markdown);
        Assert.Contains("**Legacy Platform Upgrade**", result.Markdown);
        Assert.Contains("### Lead Architect | Contoso", result.Markdown);
        Assert.Contains("**Cloud Migration Portal**", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_UsesEarlyCareerFallbackWordingWhenProjectsAreMissing()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest();
        var candidate = request.Candidate with
        {
            Experience =
            [
                new ExperienceEntry("LegacyCorp", "Junior Engineer", "Maintained legacy systems.", "Aarhus", new DateRange(new PartialDate("2003", 2003), new PartialDate("2007", 2007))),
                new ExperienceEntry("Contoso", "Lead Architect", "Led cloud migration.", "Seattle", new DateRange(new PartialDate("2019", 2019), null))
            ],
            Projects =
            [
                new ProjectEntry("Cloud Migration Portal", "Built a self-service migration portal.", null, new DateRange(new PartialDate("2021", 2021), null))
            ]
        };

        var datedRequest = request with { Candidate = candidate };

        var result = await renderer.RenderAsync(datedRequest);

        Assert.Contains("## Early Career", result.Markdown);
        Assert.Contains("**Junior Engineer** | LegacyCorp", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_UsesProfileSummarySectionOverrideWhenProvided()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest(
            generatedBody: "Fallback profile.",
            generatedSections:
            [
                new CvSectionContent(CvSection.ProfileSummary, "Section-driven profile paragraph.")
            ]);

        var result = await renderer.RenderAsync(request);

        Assert.Contains("Section-driven profile paragraph.", result.Markdown);
        Assert.DoesNotContain("Fallback profile.", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_UsesKeySkillsSectionOverrideWhenProvided()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest(
            mustHaveThemes: ["Azure"],
            evidenceTags: ["Azure"],
            generatedSections:
            [
                new CvSectionContent(CvSection.KeySkills, "Azure, .NET, Kubernetes, GitOps")
            ]);

        var result = await renderer.RenderAsync(request);

        Assert.Contains("**Key Technologies & Competencies:** Azure, .NET, Kubernetes, GitOps", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_UsesExperienceHighlightsOverrideWhenProvided()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest(
            generatedSections:
            [
                new CvSectionContent(CvSection.ExperienceHighlights,
                    "### Lead Architect | Contoso\n\n- Cut migration time 40% using infra-as-code.")
            ]);

        var result = await renderer.RenderAsync(request);

        Assert.Contains("Cut migration time 40% using infra-as-code.", result.Markdown);
        Assert.DoesNotContain("Built microservices.", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_UsesProjectHighlightsOverrideWhenProvided()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest(
            generatedSections:
            [
                new CvSectionContent(CvSection.ProjectHighlights,
                    "### Cloud Migration Portal\n\n- Saved 200 hours/month for migration teams.")
            ]);

        var result = await renderer.RenderAsync(request);

        Assert.Contains("Saved 200 hours/month for migration teams.", result.Markdown);
        Assert.DoesNotContain("Built a self-service migration portal.", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_FallsBackToGeneratedBodyWhenNoSectionsProvided()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest(generatedBody: "Backward-compatible profile.");

        var result = await renderer.RenderAsync(request);

        Assert.Contains("Backward-compatible profile.", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_IncludesEducationSection_WhenProfileHasEducation()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest();
        var withEducation = request with
        {
            Candidate = request.Candidate with
            {
                Education =
                [
                    new EducationEntry("Aarhus University", "MSc Computer Science", null, null,
                        new DateRange(new PartialDate("2008", 2008), new PartialDate("2010", 2010)))
                ]
            }
        };

        var result = await renderer.RenderAsync(withEducation);

        Assert.Contains("## Education", result.Markdown);
        Assert.Contains("**MSc Computer Science**", result.Markdown);
        Assert.Contains("Aarhus University", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_IncludesEducationSection_WithDanishHeading_ForDanishOutput()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest(outputLanguage: OutputLanguage.Danish);
        var withEducation = request with
        {
            Candidate = request.Candidate with
            {
                Education =
                [
                    new EducationEntry("Aarhus Universitet", "Cand.scient. Datalogi", null, null,
                        new DateRange(new PartialDate("2008", 2008), new PartialDate("2010", 2010)))
                ]
            }
        };

        var result = await renderer.RenderAsync(withEducation);

        Assert.Contains("## Uddannelse", result.Markdown);
        Assert.Contains("Aarhus Universitet", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_IncludesLanguagesSection_FromManualSignals()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest();
        var withLanguages = request with
        {
            Candidate = request.Candidate with
            {
                ManualSignals = new Dictionary<string, string>
                {
                    ["Languages"] = "Danish — Proficiency: Native or bilingual\nEnglish — Proficiency: Professional working"
                }
            }
        };

        var result = await renderer.RenderAsync(withLanguages);

        Assert.Contains("## Languages", result.Markdown);
        Assert.Contains("Danish", result.Markdown);
        Assert.Contains("English", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_IncludesLanguagesSection_WithDanishHeading_ForDanishOutput()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest(outputLanguage: OutputLanguage.Danish);
        var withLanguages = request with
        {
            Candidate = request.Candidate with
            {
                ManualSignals = new Dictionary<string, string>
                {
                    ["Languages"] = "Dansk — Modersmål\nEngelsk — Professionel"
                }
            }
        };

        var result = await renderer.RenderAsync(withLanguages);

        Assert.Contains("## Sprog", result.Markdown);
        Assert.Contains("Dansk", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_Cv_OmitsFitSnapshotSection()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest();
        var withFit = request with
        {
            JobFitAssessment = new JobFitAssessment(
                OverallScore: 87,
                Recommendation: JobFitRecommendation.Apply,
                Requirements: Array.Empty<JobRequirementAssessment>(),
                Strengths: ["Cloud architecture leadership"],
                Gaps: ["Kubernetes operator authoring"])
        };

        var result = await renderer.RenderAsync(withFit);

        // FitSnapshot is internal-only — must never appear in the rendered CV.
        Assert.DoesNotContain("Fit Snapshot", result.Markdown);
        Assert.DoesNotContain("Matchvurdering", result.Markdown);
        Assert.DoesNotContain("Cloud architecture leadership", result.Markdown);
    }

    [Fact]
    public async Task RenderAsync_NonCvMaterial_DoesNotAppendInternalFitOrEvidenceSections()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest(
            generatedBody: "Focused application material body.",
            evidenceTags: ["Azure"]);
        var nonCvRequest = request with
        {
            Kind = DocumentKind.CoverLetter,
            JobFitAssessment = new JobFitAssessment(
                OverallScore: 87,
                Recommendation: JobFitRecommendation.Apply,
                Requirements: Array.Empty<JobRequirementAssessment>(),
                Strengths: ["Cloud architecture leadership"],
                Gaps: ["Kubernetes operator authoring"]),
            ApplicantDifferentiatorProfile = new ApplicantDifferentiatorProfile
            {
                TargetNarrative = "Pragmatic AI architect"
            }
        };

        var result = await renderer.RenderAsync(nonCvRequest);

        Assert.Contains("## Cover Letter", result.Markdown);
        Assert.Contains("Focused application material body.", result.Markdown);
        Assert.DoesNotContain("Fit Snapshot", result.Markdown);
        Assert.DoesNotContain("Selected Evidence", result.Markdown);
        Assert.DoesNotContain("Applicant Angle", result.Markdown);
        Assert.DoesNotContain("Cloud architecture leadership", result.Markdown);
        Assert.DoesNotContain("Lead Architect @ Contoso", result.Markdown);
        Assert.Null(result.AtsSnapshot);
    }

    [Fact]
    public async Task RenderAsync_Cv_AttachesAtsSnapshotForCv()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest(mustHaveThemes: ["Azure", "Kubernetes"]);
        var withContact = request with
        {
            PersonalContact = new PersonalContactInfo(
                Email: "alex.taylor@example.com",
                Phone: "+45 00 00 00 00",
                LinkedInUrl: "https://www.linkedin.com/in/alex-taylor-demo",
                City: "Aarhus")
        };

        var result = await renderer.RenderAsync(withContact);

        Assert.NotNull(result.AtsSnapshot);
        Assert.Equal("Alex Taylor", result.AtsSnapshot!.FullName);
        Assert.Equal("Lead Cloud Architect", result.AtsSnapshot.TargetRoleTitle);
        Assert.Equal("Northwind Traders", result.AtsSnapshot.TargetCompanyName);
        Assert.Contains("Azure", result.AtsSnapshot.MustHaveThemes);
        Assert.Equal("alex.taylor@example.com", result.AtsSnapshot.Contact?.Email);
        Assert.Equal("Aarhus", result.AtsSnapshot.Contact?.City);
    }

    [Fact]
    public async Task RenderAsync_Cv_EmitsContactLineWhenPersonalContactProvided()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildCvRequest();
        var withContact = request with
        {
            PersonalContact = new PersonalContactInfo(
                Email: "alex.taylor@example.com",
                Phone: "+45 00 00 00 00",
                LinkedInUrl: "https://www.linkedin.com/in/alex-taylor-demo",
                City: "Aarhus")
        };

        var result = await renderer.RenderAsync(withContact);

        Assert.Contains("alex.taylor@example.com", result.Markdown);
        Assert.Contains("+45 00 00 00 00", result.Markdown);
        Assert.Contains("alex-taylor-demo", result.Markdown);
        Assert.Contains("Aarhus", result.Markdown);
    }

    private static DocumentRenderRequest BuildCvRequest(
        string? generatedBody = "Experienced cloud architect.",
        OutputLanguage outputLanguage = OutputLanguage.English,
        IReadOnlyList<string>? mustHaveThemes = null,
        IReadOnlyList<string>? evidenceTags = null,
        IReadOnlyList<CvSectionContent>? generatedSections = null)
    {
        var candidate = new CandidateProfile
        {
            Name = new PersonName("Alex", "Taylor"),
            Headline = "Senior Cloud Architect",
            Experience =
            [
                new ExperienceEntry("Contoso", "Lead Architect", "Led cloud migration.", "Seattle", new DateRange()),
                new ExperienceEntry("Fabrikam", "Senior Developer", "Built microservices.", "London", new DateRange())
            ],
            Projects =
            [
                new ProjectEntry("Cloud Migration Portal", "Built a self-service migration portal.", null, new DateRange())
            ],
            Recommendations =
            [
                new RecommendationEntry(
                    new PersonName("Jane", "Smith"), "Contoso", "CTO",
                    "An exceptional architect who consistently delivers impactful solutions with strong technical leadership.", "VISIBLE"),
                new RecommendationEntry(
                    new PersonName("Lars", "Madsen"), "Fabrikam", "Director",
                    "Han har altid været en dygtig og pålidelig kollega som er med til at løfte teamet og har stor erfaring med softwareudvikling.", "VISIBLE")
            ]
        };

        var jobPosting = new JobPostingAnalysis
        {
            RoleTitle = "Lead Cloud Architect",
            CompanyName = "Northwind Traders",
            Summary = "Lead our cloud platform.",
            MustHaveThemes = mustHaveThemes ?? Array.Empty<string>(),
            NiceToHaveThemes = Array.Empty<string>()
        };

        var tags = evidenceTags ?? Array.Empty<string>();
        var evidence = tags.Count > 0
            ? new EvidenceSelectionResult([
                new RankedEvidenceItem(
                    new CandidateEvidenceItem("exp-1", CandidateEvidenceType.Experience, "Lead Architect @ Contoso", "Led cloud migration.", tags),
                    50, ["Supports role"], true)])
            : EvidenceSelectionResult.Empty;

        return new DocumentRenderRequest(
            DocumentKind.Cv,
            candidate,
            jobPosting,
            null,
            generatedBody,
            outputLanguage,
            EvidenceSelection: evidence,
            GeneratedSections: generatedSections);
    }
}
