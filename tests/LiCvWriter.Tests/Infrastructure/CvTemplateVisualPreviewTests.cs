using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LiCvWriter.Infrastructure.Documents.Templates;

namespace LiCvWriter.Tests.Infrastructure;

/// <summary>
/// Generates a populated CV preview against synthetic, PII-free data. Writes
/// both the populated .docx and a one-shot summary to
/// <c>artifacts/visual-review/</c> at the repository root so a reviewer can
/// render the template visually during design iterations.
/// </summary>
/// <remarks>
/// This test is <c>Skip</c>-gated by default because it produces files outside
/// the test output directory. Run it explicitly when you need to regenerate
/// the preview:
/// <code>dotnet test --filter "FullyQualifiedName~CvTemplateVisualPreview" /p:VisualReview=true</code>
/// </remarks>
public sealed class CvTemplateVisualPreviewTests
{
    [Fact]
    public void GeneratePopulatedPreview_WritesReviewableDocx()
    {
        if (Environment.GetEnvironmentVariable("LICV_VISUAL_REVIEW") is null)
        {
            return;
        }

        var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "LiCvWriter.sln")))
        {
            repoRoot = repoRoot.Parent;
        }
        Assert.NotNull(repoRoot);

        var templatePath = Path.Combine(repoRoot.FullName,
            "src", "LiCvWriter.Infrastructure", "Documents", "Templates", "cv-template.dotx");
        var reviewFolder = Path.Combine(repoRoot.FullName, "artifacts", "visual-review");
        Directory.CreateDirectory(reviewFolder);

        // Ensure the template on disk reflects the current generator output.
        CvWordTemplateGenerator.Generate(templatePath);

        var previewPath = Path.Combine(reviewFolder, "cv-template-populated-preview.docx");
        if (File.Exists(previewPath))
        {
            File.Delete(previewPath);
        }

        File.Copy(templatePath, previewPath);

        using (var package = WordprocessingDocument.Open(previewPath, isEditable: true))
        {
            package.ChangeDocumentType(DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        }

        using (var doc = WordprocessingDocument.Open(previewPath, isEditable: true))
        {
            var mainPart = doc.MainDocumentPart!;

            var populated = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (tag, markdown) in SampleSections)
            {
                if (TemplateContentPopulator.PopulateContentControl(mainPart, tag, markdown))
                {
                    populated.Add(tag);
                }
            }

            var body = mainPart.Document!.Body!;
            TemplateContentPopulator.RemoveEmptyControls(body, populated);
            TemplateContentPopulator.UnwrapAllSdtBlocks(body);
            mainPart.Document.Save();
        }

        Assert.True(File.Exists(previewPath));
        // Sanity floor: a populated preview should always exceed the empty-template
        // baseline (~3 KB). Well below the observed ~5 KB populated size.
        Assert.True(new FileInfo(previewPath).Length > 4_000);
    }

    /// <summary>
    /// Synthetic sample content per section tag. All names, emails, phone
    /// numbers and companies are fictional — no PII.
    /// </summary>
    private static readonly IReadOnlyList<(string Tag, string Markdown)> SampleSections =
    [
        ("CandidateHeader",
            """
            # Jane Doe
            Senior Software Engineer · Cloud & Distributed Systems

            Copenhagen, Denmark · jane.doe@example.com · +45 00 00 00 00 · linkedin.com/in/jane-doe-demo
            """),
        ("ProfileSummary",
            """
            ## Professional Profile

            Senior engineer with 12+ years building reliable distributed systems on .NET, Azure and Kubernetes. Led the modernization of a payment platform serving 4M customers, cutting mean-time-to-recovery by 68% and raising deployment frequency from monthly to daily. Known for pairing rigorous technical judgment with pragmatic delivery and for mentoring engineers across multiple squads.
            """),
        ("KeySkills",
            """
            ## Key Technologies & Competencies

            C# 13, .NET 9, ASP.NET Core, Azure (AKS, Service Bus, Key Vault, Functions), Kubernetes, Terraform, PostgreSQL, Event-driven architecture, Distributed systems, OpenTelemetry, gRPC, Domain-Driven Design, CQRS, CI/CD (GitHub Actions), Technical leadership
            """),
        ("Experience",
            """
            ## Professional Experience

            ### Principal Engineer | Contoso Payments A/S | Mar 2021 – Present

            - Led re-architecture of a monolithic settlement engine into event-driven services on AKS, cutting deployment lead time from 12 days to 4 hours.
            - Introduced OpenTelemetry tracing across 23 services; reduced MTTR from 47 min to 15 min within 2 quarters.
            - Mentored 6 senior engineers; 3 promoted to staff-level within 18 months.

            ### Senior Engineer | Fabrikam ApS | Aug 2017 – Feb 2021

            - Delivered a multi-tenant onboarding API handling 1.8M requests/day at p99 < 120 ms.
            - Chaired the architecture review board; published 14 ADRs still referenced by the platform team.

            ### Software Engineer | Northwind Traders Nordic | Jun 2013 – Jul 2017

            - Rewrote legacy VBScript batch jobs into a .NET workflow engine, eliminating 40 hours/month of manual operator work.
            """),
        ("Projects",
            """
            ## Projects

            ### Open-source: dotnet-event-inspector | 2023 – Present

            - Built a CLI for post-hoc inspection of Azure Service Bus dead-letter queues; 1.4k GitHub stars, 180k NuGet downloads.

            ### Kubernetes cost-attribution toolkit | 2022

            - Shipped a Helm-packaged Prometheus exporter that attributes AKS pod costs to product teams; adopted by 4 internal platforms.
            """),
        ("Education",
            """
            ## Education

            - **MSc, Computer Science** — Technical University of Denmark (DTU), 2011 – 2013
            - **BSc, Software Engineering** — Aarhus University, 2008 – 2011
            """),
        ("Certifications",
            """
            ## Certifications

            - Microsoft Certified: Azure Solutions Architect Expert (2024)
            - Certified Kubernetes Administrator, CNCF (2022)
            """),
        ("Languages",
            """
            ## Languages

            - Danish — Native
            - English — Professional working proficiency
            - German — Conversational
            """),
        ("Recommendations",
            """
            ## Recommendations

            > *"Jane combines rare technical depth with an unusual ability to simplify trade-offs for non-technical stakeholders. Our modernization programme would not have landed on time without her."*
            > — Alex Rivera, VP Engineering at Contoso Payments A/S

            > *"One of the top three engineers I've worked with in 20 years. Great mentor, meticulous designer, and calm under pressure."*
            > — Sam Lind, Staff Engineer at Fabrikam ApS
            """),
        ("EarlyCareer",
            """
            ## Early Career

            - **Junior Developer** | Litware Systems | 2008 – 2010
            - **Student Assistant** | Aarhus University IT | 2006 – 2008
            """),
    ];
}
