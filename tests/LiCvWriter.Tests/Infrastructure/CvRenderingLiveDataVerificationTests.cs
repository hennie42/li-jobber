using DocumentFormat.OpenXml.Packaging;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Infrastructure.Documents;

namespace LiCvWriter.Tests.Infrastructure;

/// <summary>
/// Renders a full CV using production-representative profile data and asserts formatting
/// invariants. The rendered Markdown is written to disk so visual inspection is trivial.
/// Run this test repeatedly while iterating on renderer layout logic.
/// </summary>
public sealed class CvRenderingLiveDataVerificationTests
{
    private static readonly string OutputDirectory = Path.Combine(
        Path.GetTempPath(), "licvwriter-render-verify");

    /// <summary>
    /// Renders the CV with representative data and writes it to disk for inspection.
    /// Asserts all structural invariants that must hold for a correctly formatted CV.
    /// </summary>
    [Fact]
    public async Task RenderCv_WithRepresentativeData_PassesAllFormattingInvariants()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildRepresentativeRequest();

        var result = await renderer.RenderAsync(request);
        var markdown = result.Markdown;

        Directory.CreateDirectory(OutputDirectory);
        var outputPath = Path.Combine(OutputDirectory, "rendered-cv.md");
        await File.WriteAllTextAsync(outputPath, markdown);

        // ── Section ordering ──────────────────────────────────────────────
        var sectionPositions = new Dictionary<string, int>
        {
            ["Professional Profile"] = markdown.IndexOf("## Professional Profile", StringComparison.Ordinal),
            ["Professional Experience"] = markdown.IndexOf("## Professional Experience", StringComparison.Ordinal),
            ["Early Career"] = markdown.IndexOf("## Early Career", StringComparison.Ordinal),
            ["Certifications"] = markdown.IndexOf("## Certifications", StringComparison.Ordinal),
            ["Recommendations"] = markdown.IndexOf("## Recommendations", StringComparison.Ordinal),
        };

        Assert.True(sectionPositions["Professional Profile"] >= 0, "Missing: ## Professional Profile");
        Assert.True(sectionPositions["Professional Experience"] >= 0, "Missing: ## Professional Experience");
        Assert.True(sectionPositions["Early Career"] >= 0, "Missing: ## Early Career");
        Assert.True(sectionPositions["Certifications"] >= 0, "Missing: ## Certifications");
        Assert.True(sectionPositions["Recommendations"] >= 0, "Missing: ## Recommendations");

        Assert.True(
            sectionPositions["Professional Experience"] > sectionPositions["Professional Profile"],
            "Experience must come after Profile");
        Assert.True(
            sectionPositions["Certifications"] > sectionPositions["Professional Experience"],
            "Certifications must come after Professional Experience");
        Assert.True(
            sectionPositions["Recommendations"] > sectionPositions["Certifications"],
            "Recommendations must come after Certifications");
        Assert.True(
            sectionPositions["Early Career"] > sectionPositions["Recommendations"],
            "Early Career must come last (after Recommendations)");

        // ── Modern experience (post-2008): must appear under Professional Experience ──
        // Modern experience now ends before Certifications (Education sits in between
        // when the candidate has it; the test fixture currently doesn't, so we use
        // Certifications as the always-present upper bound).
        AssertBetween(markdown, "## Professional Experience", "## Certifications",
            "Senior Automation Architect", "Modern role must be under Professional Experience");
        AssertBetween(markdown, "## Professional Experience", "## Certifications",
            "Senior Cloud Architect and Advisor", "Modern role must be under Professional Experience");
        AssertBetween(markdown, "## Professional Experience", "## Certifications",
            "Principal Consultant", "Modern role must be under Professional Experience");
        AssertBetween(markdown, "## Professional Experience", "## Certifications",
            "Senior IT Consultant", "Freelance role (2008-2020) must be under Professional Experience");

        // ── Early career (ended before 2009): must appear under Early Career ──
        // Early Career is now the last section so use end-of-document as the upper bound.
        AssertBetween(markdown, "## Early Career", null,
            "Software Architect", "Legacy Design Studio role (2005-2008) must be under Early Career");
        AssertBetween(markdown, "## Early Career", null,
            "Alpine Systems", "Alpine Systems role (2000-2005) must be under Early Career");
        AssertBetween(markdown, "## Early Career", null,
            "Lead Developer", "Proseware role (1999-2000) must be under Early Career");
        AssertBetween(markdown, "## Early Career", null,
            "Tailspin Interactive", "Tailspin Interactive role (1998-1999) must be under Early Career");
        AssertBetween(markdown, "## Early Career", null,
            "Wide World Web", "Wide World Web role (1995-1998) must be under Early Career");

        // ── Early career must NOT appear under Professional Experience ──
        AssertNotBetween(markdown, "## Professional Experience", "## Certifications",
            "Legacy Design Studio", "Legacy Design Studio role should NOT appear in Professional Experience");
        AssertNotBetween(markdown, "## Professional Experience", "## Certifications",
            "Alpine Systems", "Alpine Systems role should NOT appear in Professional Experience");
        AssertNotBetween(markdown, "## Professional Experience", "## Certifications",
            "Proseware", "Proseware role should NOT appear in Professional Experience");

        // ── Projects as bullet items (not ### headings) ──
        Assert.DoesNotContain("### Cloud Migration Portal", markdown);
        Assert.DoesNotContain("### Graphic Grove", markdown);

        // ── Umbrella role: Freelance should contain client sub-items ──
        AssertBetween(markdown, "Senior IT Consultant", "## Certifications",
            "**Client:", "Freelance umbrella role must have Client: sub-items");

        // ── Recommendations as italic blockquotes ──
        Assert.Contains("> *\"", markdown);
        Assert.Contains("> —", markdown);

        // ── No ### headings for individual recommendations ──
        Assert.DoesNotContain("### Jane", markdown);
        Assert.DoesNotContain("### Lars", markdown);

        // ── Early career entries as bullet items (not ### headings) ──
        // Early Career is the last section, so extract from its heading to end of document.
        var earlyCareerSection = ExtractSection(markdown, "## Early Career", null);
        Assert.DoesNotContain("###", earlyCareerSection);
        Assert.Contains("- **", earlyCareerSection);
    }

    /// <summary>
    /// Full pipeline: render markdown, export to .docx via template, and verify
    /// that all sections are present in the Word document body text.
    /// </summary>
    [Fact]
    public async Task ExportCv_WithRepresentativeData_AllSectionsAppearInDocx()
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildRepresentativeRequest();
        var rendered = await renderer.RenderAsync(request);
        var markdown = rendered.Markdown;

        var exportRoot = Path.Combine(Path.GetTempPath(), $"licvwriter-export-verify-{Guid.NewGuid():N}");
        try
        {
            var service = new TemplateBasedDocumentExportService(new StorageOptions { ExportRoot = exportRoot });
            var exportResult = await service.ExportAsync(rendered);

            Assert.True(File.Exists(exportResult.FilePath), "Exported .docx must exist");

            // Write markdown next to the docx for easy comparison
            var mdPath = Path.ChangeExtension(exportResult.FilePath, ".md");
            await File.WriteAllTextAsync(mdPath, markdown);

            using var wordDoc = WordprocessingDocument.Open(exportResult.FilePath, isEditable: false);
            var body = wordDoc.MainDocumentPart?.Document?.Body;
            Assert.NotNull(body);
            var allText = body.InnerText;

            // Write the plain text dump for diagnostics
            var txtPath = Path.ChangeExtension(exportResult.FilePath, ".txt");
            await File.WriteAllTextAsync(txtPath, allText);

            // Modern experience must be present
            Assert.Contains("Senior Automation Architect", allText);
            Assert.Contains("Senior Cloud Architect", allText);
            Assert.Contains("Principal Consultant", allText);
            Assert.Contains("Senior IT Consultant", allText);

            // Client sub-items from the freelance umbrella
            Assert.Contains("A. Datum Capital", allText);
            Assert.Contains("Graphic Grove", allText);

            // Early career must be present
            Assert.Contains("Early Career", allText);
            Assert.Contains("Legacy Design Studio", allText);
            Assert.Contains("Alpine Systems", allText);
            Assert.Contains("Proseware", allText);

            // Certifications
            Assert.Contains("Certifications", allText);
            Assert.Contains("Certified Ethical Hacker", allText);

            // Recommendations
            Assert.Contains("Recommendations", allText);
            Assert.Contains("Alex", allText);
        }
        finally
        {
            if (Directory.Exists(exportRoot))
            {
                // Leave files for manual inspection — don't clean up
            }
        }
    }

    /// <summary>
    /// Specifically verifies the early career cutoff boundary: roles ending before 2009
    /// are early career, roles ending 2009+ or ongoing are modern.
    /// </summary>
    [Theory]
    [InlineData(2005, 2008, true, "Ended 2008 → early career")]
    [InlineData(2008, 2020, false, "Ended 2020 → modern (spans boundary)")]
    [InlineData(2008, null, false, "Ongoing → modern")]
    [InlineData(2000, 2005, true, "Ended 2005 → early career")]
    [InlineData(2019, null, false, "Ongoing recent → modern")]
    [InlineData(2006, 2009, false, "Ended 2009 → modern (at boundary)")]
    public async Task EarlyCareerCutoff_ClassifiesRolesCorrectly(
        int startYear, int? endYear, bool expectEarlyCareer, string scenario)
    {
        var renderer = new MarkdownDocumentRenderer();
        var request = BuildMinimalRequest(
            new ExperienceEntry("TestCorp", "Test Role", "Did things.", "Remote",
                new DateRange(
                    new PartialDate($"Jan {startYear}", startYear, 1),
                    endYear is not null ? new PartialDate($"Dec {endYear}", endYear.Value, 12) : null)),
            new ExperienceEntry("ModernCo", "Modern Role", "Current work.", "Remote",
                new DateRange(new PartialDate("Jan 2020", 2020, 1), null)));

        var result = await renderer.RenderAsync(request);
        var markdown = result.Markdown;

        if (expectEarlyCareer)
        {
            Assert.True(markdown.Contains("## Early Career"), $"{scenario}: Expected Early Career section");
            AssertBetween(markdown, "## Early Career", null,
                "Test Role", $"{scenario}: Test Role must be under Early Career");
        }
        else
        {
            AssertBetween(markdown, "## Professional Experience", "## Early Career",
                "Test Role", $"{scenario}: Test Role must be under Professional Experience");
        }
    }

    private static DocumentRenderRequest BuildRepresentativeRequest()
    {
        var candidate = new CandidateProfile
        {
            Name = new PersonName("Alex", "Taylor"),
            Headline = "Senior Automation Architect | Cloud & Security | .NET, Azure, DevOps",
            Summary = "Experienced architect with 25+ years in software development.",
            Experience =
            [
                new ExperienceEntry("Northwind Health", "Senior Automation Architect",
                    "Leading automation initiatives across the enterprise.",
                    "Aarhus", new DateRange(new PartialDate("Oct 2025", 2025, 10), null)),

                new ExperienceEntry("Northwind Health", "Senior Cloud Architect and Advisor",
                    "Designed and implemented cloud-native solutions on Azure.",
                    "Aarhus", new DateRange(new PartialDate("Apr 2022", 2022, 4), new PartialDate("Oct 2025", 2025, 10))),

                new ExperienceEntry("Fabrikam Advisory", "Principal Consultant",
                    "Delivered digital transformation projects for enterprise clients.",
                    "Copenhagen", new DateRange(new PartialDate("Aug 2020", 2020, 8), new PartialDate("Apr 2022", 2022, 4))),

                new ExperienceEntry("Freelance", "Senior IT Consultant",
                    "Independent consulting spanning security, integration, and cloud architecture.",
                    "Copenhagen", new DateRange(new PartialDate("May 2008", 2008, 5), new PartialDate("Aug 2020", 2020, 8))),

                new ExperienceEntry("Legacy Design Studio", "Software Architect",
                    null, "Copenhagen",
                    new DateRange(new PartialDate("Mar 2005", 2005, 3), new PartialDate("Feb 2008", 2008, 2))),

                new ExperienceEntry("Alpine Systems A/S", "Senior Consultant",
                    null, "Copenhagen",
                    new DateRange(new PartialDate("Feb 2000", 2000, 2), new PartialDate("Feb 2005", 2005, 2))),

                new ExperienceEntry("Proseware A/S", "Lead Developer",
                    null, "Kolding",
                    new DateRange(new PartialDate("1999", 1999), new PartialDate("2000", 2000))),

                new ExperienceEntry("Tailspin Interactive", "Developer",
                    null, "Copenhagen",
                    new DateRange(new PartialDate("1998", 1998), new PartialDate("1999", 1999))),

                new ExperienceEntry("Wide World Web", "Consultant",
                    null, "Kolding",
                    new DateRange(new PartialDate("1995", 1995), new PartialDate("1998", 1998)))
            ],
            Projects =
            [
                new ProjectEntry("A. Datum Capital - SharePoint 2007 - SharePoint 2010", "Migration and upgrade.", null,
                    new DateRange(new PartialDate("2009", 2009), new PartialDate("2010", 2010))),
                new ProjectEntry("A. Datum Capital - Single Sign-on, SAML v2.0", "SSO implementation.", null,
                    new DateRange(new PartialDate("2010", 2010), new PartialDate("2012", 2012))),
                new ProjectEntry("Various mobile applications with social media integration", "Mobile app development.", null,
                    new DateRange(new PartialDate("2012", 2012), new PartialDate("2012", 2012))),
                new ProjectEntry("Fabrikam Foods - Analysis and specification of software", "Software analysis.", null,
                    new DateRange(new PartialDate("2012", 2012), new PartialDate("2012", 2012))),
                new ProjectEntry("Litware Denmark - Extranet with SAP integration", "SAP integration project.", null,
                    new DateRange(new PartialDate("2013", 2013), new PartialDate("2013", 2013))),
                new ProjectEntry("A. Datum Capital - Authorization with OAuth2", "OAuth2 authorization.", null,
                    new DateRange(new PartialDate("2013", 2013), new PartialDate("2014", 2014))),
                new ProjectEntry("CivicMail A/S", "Digital mailbox platform.", null,
                    new DateRange(new PartialDate("2014", 2014), new PartialDate("2014", 2014))),
                new ProjectEntry("Graphic Grove - eCommerce", "E-commerce platform.", null,
                    new DateRange(new PartialDate("2014", 2014), new PartialDate("2017", 2017))),
                new ProjectEntry("Graphic Grove - eCommerce Biztalk integration", "BizTalk integration.", null,
                    new DateRange(new PartialDate("2016", 2016), new PartialDate("2017", 2017))),
                new ProjectEntry("Global retail brand - BizTalk, EDI with AX2012", "BizTalk and EDI.", null,
                    new DateRange(new PartialDate("2017", 2017), new PartialDate("2018", 2018))),
                new ProjectEntry("Global retail brand - Azure API, AD, CosmosDb, Functions and global scale", "Azure platform.", null,
                    new DateRange(new PartialDate("2018", 2018), new PartialDate("2018", 2018))),
                new ProjectEntry("Municipal Software A/S - OIOIDWS", "Danish standard web services.", null,
                    new DateRange(new PartialDate("2018", 2018), new PartialDate("2018", 2018))),
                new ProjectEntry("A. Datum Capital - CIAM, SSO, OAuth, SAML and Azure ARM templates", "Identity platform.", null,
                    new DateRange(new PartialDate("2018", 2018), new PartialDate("2019", 2019))),
                new ProjectEntry("Roadside Services A/S - Azure architect, integration consultant and developer", "Azure architecture.", null,
                    new DateRange(new PartialDate("2019", 2019), new PartialDate("2019", 2019)))
            ],
            Certifications =
            [
                new CertificationEntry("Certified Ethical Hacker (CEH)", "EC-Council", null,
                    new DateRange(new PartialDate("2014", 2014)), null),
                new CertificationEntry("PRINCE2 Agile® Foundation & Practitioner", "AXELOS Global Best Practice", null,
                    new DateRange(new PartialDate("2021", 2021)), null),
                new CertificationEntry("Strategic Specialist", "Leadership Pipeline Institute", null,
                    new DateRange(new PartialDate("2025", 2025)), null)
            ],
            Recommendations =
            [
                new RecommendationEntry(
                    new PersonName("John", "Smith"), "Fabrikam Foods", "Head of Enterprise Architecture",
                    "I know Alex to be a very competent and trust worthy cloud architect and advisor. He combines deep technical skills with business understanding.",
                    "VISIBLE"),
                new RecommendationEntry(
                    new PersonName("Lars", "Nielsen"), "RetailCo i Danmark", "Agile Release & Change Manager",
                    "Alex er kvik og behagelig at arbejde sammen med. Der sker noget, og opgaverne bliver løst med et smil.",
                    "VISIBLE"),
                new RecommendationEntry(
                    new PersonName("Jane", "Morgan"), "Northwind Health", "Head of Cloud Center of Excellence",
                    "I have had the pleasure of working with Alex both as coworker and in my role as his manager. Truly exceptional.",
                    "VISIBLE"),
                new RecommendationEntry(
                    new PersonName("Peter", "Hansen"), "Delivery Works", "Managing Director, Technology",
                    "I have worked with Alex on several occasions and can give him my best recommendation.",
                    "VISIBLE")
            ],
            Skills =
            [
                new SkillTag("Azure", 1),
                new SkillTag(".NET", 2),
                new SkillTag("C#", 3),
                new SkillTag("Architecture", 4),
                new SkillTag("Security", 5),
                new SkillTag("DevOps", 6)
            ]
        };

        var jobPosting = new JobPostingAnalysis
        {
            RoleTitle = "Senior Backend Engineer (Architect/Principal)",
            CompanyName = "Northwind Software",
            Summary = "Seeking a highly skilled Senior Backend Engineer to drive technical excellence.",
            MustHaveThemes = ["Architecture", "Security", "Quality engineering", "Leadership", ".NET", "C#"],
            NiceToHaveThemes = ["Azure", "Communication"]
        };

        var certificationEvidence = candidate.Certifications.Select((cert, i) =>
            new RankedEvidenceItem(
                new CandidateEvidenceItem($"cert-{i}", CandidateEvidenceType.Certification, cert.Name,
                    cert.Authority ?? "", ["Certification"]),
                80 - (i * 5), ["Relevant certification"], true)).ToArray();

        var recommendationEvidence = candidate.Recommendations.Select((rec, i) =>
            new RankedEvidenceItem(
                new CandidateEvidenceItem($"rec-{i}", CandidateEvidenceType.Recommendation,
                    $"Recommendation from {rec.Author.FullName}", rec.Text[..Math.Min(100, rec.Text.Length)],
                    ["Recommendation"]),
                90 - (i * 5), ["Strong recommendation"], true)).ToArray();

        var allEvidence = certificationEvidence.Concat(recommendationEvidence).ToArray();
        var evidenceSelection = new EvidenceSelectionResult(allEvidence);

        return new DocumentRenderRequest(
            DocumentKind.Cv,
            candidate,
            jobPosting,
            "Northwind Software is a software company focused on document automation.",
            GeneratedBody: "Architect with 25+ years delivering enterprise solutions in .NET, Azure, and security.",
            OutputLanguage: OutputLanguage.English,
            EvidenceSelection: evidenceSelection);
    }

    private static DocumentRenderRequest BuildMinimalRequest(params ExperienceEntry[] experience)
    {
        var candidate = new CandidateProfile
        {
            Name = new PersonName("Test", "User"),
            Experience = experience
        };

        var jobPosting = new JobPostingAnalysis
        {
            RoleTitle = "Engineer",
            CompanyName = "TestCo"
        };

        return new DocumentRenderRequest(
            DocumentKind.Cv,
            candidate,
            jobPosting,
            null,
            GeneratedBody: "Test profile.");
    }

    private static void AssertBetween(string markdown, string startSection, string? endSection, string expected, string message)
    {
        var startIndex = markdown.IndexOf(startSection, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Section '{startSection}' not found. {message}");

        var endIndex = endSection is not null
            ? markdown.IndexOf(endSection, startIndex + startSection.Length, StringComparison.Ordinal)
            : markdown.Length;

        if (endIndex < 0) endIndex = markdown.Length;

        var section = markdown[startIndex..endIndex];
        Assert.True(section.Contains(expected, StringComparison.Ordinal),
            $"{message}\nExpected '{expected}' between '{startSection}' and '{endSection ?? "EOF"}'.\nSection content:\n{section}");
    }

    private static void AssertNotBetween(string markdown, string startSection, string? endSection, string forbidden, string message)
    {
        var startIndex = markdown.IndexOf(startSection, StringComparison.Ordinal);
        if (startIndex < 0) return;

        var endIndex = endSection is not null
            ? markdown.IndexOf(endSection, startIndex + startSection.Length, StringComparison.Ordinal)
            : markdown.Length;

        if (endIndex < 0) endIndex = markdown.Length;

        var section = markdown[startIndex..endIndex];
        Assert.DoesNotContain(forbidden, section);
    }

    private static string ExtractSection(string markdown, string startHeader, string? endHeader)
    {
        var startIndex = markdown.IndexOf(startHeader, StringComparison.Ordinal);
        if (startIndex < 0) return string.Empty;

        var endIndex = endHeader is not null
            ? markdown.IndexOf(endHeader, startIndex + startHeader.Length, StringComparison.Ordinal)
            : markdown.Length;

        if (endIndex < 0) endIndex = markdown.Length;

        return markdown[startIndex..endIndex];
    }
}

