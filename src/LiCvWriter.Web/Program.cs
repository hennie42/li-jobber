using System.Net;
using System.Text.Json;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Infrastructure.Csv;
using LiCvWriter.Infrastructure.Documents;
using LiCvWriter.Infrastructure.LinkedIn;
using LiCvWriter.Infrastructure.Llm;
using LiCvWriter.Infrastructure.Research;
using LiCvWriter.Infrastructure.Storage;
using LiCvWriter.Infrastructure.Workflows;
using LiCvWriter.Web.Components;
using LiCvWriter.Web.Services;

var builder = WebApplication.CreateBuilder(args);

TryEnableStaticWebAssets(builder);

builder.Configuration.AddUserSecrets<Program>(optional: true);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

var linkedInOptions = builder.Configuration.GetSection(LinkedInAuthOptions.SectionName).Get<LinkedInAuthOptions>() ?? new LinkedInAuthOptions();
var jobDiscoveryOptions = builder.Configuration.GetSection(JobDiscoveryOptions.SectionName).Get<JobDiscoveryOptions>() ?? new JobDiscoveryOptions();
var ollamaOptions = builder.Configuration.GetSection(OllamaOptions.SectionName).Get<OllamaOptions>() ?? new OllamaOptions();
var storageOptions = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();

builder.Services.AddSingleton(linkedInOptions);
builder.Services.AddSingleton(jobDiscoveryOptions);
builder.Services.AddSingleton(ollamaOptions);
builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton<SimpleCsvParser>();
builder.Services.AddSingleton<LinkedInPartialDateParser>();
builder.Services.AddSingleton<CandidateProfileMergeService>();
builder.Services.AddSingleton<JobDiscoveryProfileLightService>();
builder.Services.AddSingleton<JobDiscoverySearchPlanService>();
builder.Services.AddScoped<JobDiscoverySuggestionService>();
var defaultSelectedEvidenceCount = builder.Configuration.GetValue("Evidence:DefaultSelectedCount", 30);

builder.Services.AddSingleton<CandidateEvidenceService>();
builder.Services.AddSingleton<JobFitAnalysisService>();
builder.Services.AddSingleton(provider =>
    new EvidenceSelectionService(provider.GetRequiredService<CandidateEvidenceService>(), defaultSelectedEvidenceCount));
builder.Services.AddSingleton<WorkspaceRecoveryStore>();
builder.Services.AddSingleton<OperationStatusService>();
builder.Services.AddSingleton<LlmOperationBroker>();
builder.Services.AddScoped<LlmTechnologyGapAnalysisService>();
builder.Services.AddScoped<LlmFitEnhancementService>();
builder.Services.AddScoped<InsightsDiscoveryApplicantDifferentiatorDraftingService>();
builder.Services.AddSingleton<WorkspaceSession>();
builder.Services.AddScoped<JobFitWorkspaceRefreshService>();
builder.Services.AddSingleton<ILinkedInExportImporter, LinkedInExportImporter>();
builder.Services.AddSingleton<IInsightsDiscoveryPdfImporter, InsightsDiscoveryPdfImporter>();
builder.Services.AddSingleton<IAuditStore, LocalMarkdownAuditStore>();
builder.Services.AddSingleton<IDocumentRenderer, MarkdownDocumentRenderer>();
builder.Services.AddSingleton<CvQualityValidator>();
builder.Services.AddSingleton<IDocumentExportService, TemplateBasedDocumentExportService>();
builder.Services.AddScoped<IDraftGenerationService, DraftGenerationService>();

builder.Services.AddHttpClient<LinkedInMemberSnapshotImporter>();
builder.Services.AddHttpClient<OllamaClient>(client =>
{
    client.BaseAddress = NormalizeApiBase(ollamaOptions.BaseUrl);
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddScoped<PromptCapturingLlmClient>(provider =>
    new PromptCapturingLlmClient(provider.GetRequiredService<OllamaClient>()));
builder.Services.AddScoped<ILlmClient>(provider =>
    provider.GetRequiredService<PromptCapturingLlmClient>());
builder.Services.AddScoped<OllamaCapacityProbe>();
builder.Services.AddScoped<OllamaModelBenchmarkService>();
builder.Services.AddSingleton<ModelBenchmarkCoordinator>();

builder.Services.AddHttpClient<IJobResearchService, HttpJobResearchService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(1);
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    });

builder.Services.AddHttpClient<IJobDiscoveryService, HttpJobDiscoveryService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    });

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapStaticAssets();

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
        }

        return Task.CompletedTask;
    });

    await next();
});

app.MapGet("/api/health/ollama", async (ILlmClient client, CancellationToken cancellationToken) =>
{
    var result = await client.VerifyModelAvailabilityAsync(cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/api/llm/operations/generate-drafts", (StartDraftGenerationOperationRequest request, LlmOperationBroker broker) =>
{
    try
    {
        return Results.Ok(broker.StartDraftGeneration(request));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(exception.Message);
    }
});

app.MapPost("/api/llm/operations/job-context", (StartJobContextOperationRequest request, LlmOperationBroker broker) =>
{
    try
    {
        return Results.Ok(broker.StartJobContextAnalysis(request));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(exception.Message);
    }
});

app.MapPost("/api/llm/operations/technology-gap", (StartTechnologyGapOperationRequest request, LlmOperationBroker broker) =>
{
    try
    {
        return Results.Ok(broker.StartTechnologyGapAnalysis(request));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(exception.Message);
    }
});

app.MapPost("/api/llm/operations/fit-review", (StartFitReviewOperationRequest request, LlmOperationBroker broker) =>
{
    try
    {
        return Results.Ok(broker.StartFitReviewAnalysis(request));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(exception.Message);
    }
});

app.MapPost("/api/llm/operations/refresh-all", (StartRefreshAllOperationRequest request, LlmOperationBroker broker) =>
{
    try
    {
        return Results.Ok(broker.StartRefreshAllAnalysis(request));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(exception.Message);
    }
});

app.MapGet("/api/llm/operations/{operationId}", (string operationId, LlmOperationBroker broker) =>
{
    var snapshot = broker.GetSnapshot(operationId);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
});

app.MapPost("/api/llm/operations/{operationId}/cancel", (string operationId, LlmOperationBroker broker) =>
    broker.Cancel(operationId)
        ? Results.Accepted($"/api/llm/operations/{operationId}")
        : Results.NotFound());

app.MapGet("/api/llm/operations/{operationId}/events", async Task<IResult> (string operationId, HttpContext context, LlmOperationBroker broker, CancellationToken cancellationToken) =>
{
    if (broker.GetSnapshot(operationId) is null)
    {
        return Results.NotFound();
    }

    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Append("X-Accel-Buffering", "no");
    context.Response.ContentType = "text/event-stream";

    await foreach (var operationEvent in broker.StreamEventsAsync(operationId, cancellationToken))
    {
        var json = JsonSerializer.Serialize(operationEvent);
        await context.Response.WriteAsync($"event: {operationEvent.EventType}\n", cancellationToken);
        await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    return Results.Empty;
});

if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Playwright:EnableDemoSeed"))
{
    app.MapPost("/api/playwright/demo-seed", async Task<IResult> (
        WorkspaceSession workspace,
        ILlmClient llmClient,
        JobFitWorkspaceRefreshService fitRefreshService,
        OllamaOptions options,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        var availability = await llmClient.VerifyModelAvailabilityAsync(cancellationToken);
        if (!availability.Installed || availability.AvailableModels.Count == 0)
        {
            return Results.BadRequest("Ollama is reachable, but no installed model is available for the Playwright demo seed.");
        }

        workspace.SetOllamaAvailability(availability);
        var selectedModel = SelectDemoModel(availability, options.Model);
        workspace.SetLlmSessionSettings(selectedModel, options.Think);
        SeedPlaywrightDemoWorkspace(workspace);

        if (IsFullPlaywrightDemo(httpContext))
        {
            SeedPlaywrightFullDemoWorkspace(workspace, fitRefreshService, selectedModel);
        }

        return Results.Ok(new PlaywrightDemoSeedResult(
            selectedModel,
            PlaywrightDemoSeedData.CompanyNames,
            workspace.JobSets.OrderBy(static jobSet => jobSet.SortOrder).Take(3).Select(static jobSet => jobSet.Id).ToArray()));
    });
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode();

app.Run();

static Uri NormalizeApiBase(string baseUrl)
{
    var normalized = string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:11434/api/" : baseUrl.Trim();
    if (!normalized.EndsWith('/'))
    {
        normalized += "/";
    }

    return new Uri(normalized, UriKind.Absolute);
}

static void TryEnableStaticWebAssets(WebApplicationBuilder builder)
{
    if (!string.IsNullOrWhiteSpace(builder.Configuration[Microsoft.AspNetCore.Hosting.WebHostDefaults.StaticWebAssetsKey]))
    {
        return;
    }

    var objDirectory = Path.Combine(builder.Environment.ContentRootPath, "obj");
    if (!Directory.Exists(objDirectory))
    {
        return;
    }

    var manifestPath = Directory.EnumerateFiles(objDirectory, "staticwebassets.development.json", SearchOption.AllDirectories)
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();

    if (string.IsNullOrWhiteSpace(manifestPath))
    {
        return;
    }

    builder.Configuration[Microsoft.AspNetCore.Hosting.WebHostDefaults.StaticWebAssetsKey] = manifestPath;
    Microsoft.AspNetCore.Hosting.StaticWebAssets.StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);
}

static string SelectDemoModel(OllamaModelAvailability availability, string configuredModel)
{
    var configuredMatch = availability.AvailableModels.FirstOrDefault(model => model.Equals(configuredModel, StringComparison.OrdinalIgnoreCase));
    return configuredMatch ?? availability.Model ?? availability.AvailableModels[0];
}

static bool IsFullPlaywrightDemo(HttpContext httpContext)
    => string.Equals(httpContext.Request.Query["scope"].ToString(), "full", StringComparison.OrdinalIgnoreCase);

static void SeedPlaywrightDemoWorkspace(WorkspaceSession workspace)
{
    workspace.UpdateCandidateProfile(PlaywrightDemoSeedData.CandidateProfile);

    while (workspace.JobSets.Count < PlaywrightDemoSeedData.JobPostings.Length)
    {
        workspace.AddJobSet();
    }

    foreach (var extraJobSet in workspace.JobSets.OrderBy(static jobSet => jobSet.SortOrder).Skip(PlaywrightDemoSeedData.JobPostings.Length).ToArray())
    {
        workspace.DeleteJobSet(extraJobSet.Id);
    }

    var seededJobSets = workspace.JobSets.OrderBy(static jobSet => jobSet.SortOrder).Take(PlaywrightDemoSeedData.JobPostings.Length).ToArray();
    for (var index = 0; index < seededJobSets.Length; index++)
    {
        var jobSet = seededJobSets[index];
        workspace.UpdateJobSetInputs(jobSet.Id, string.Empty, string.Empty, string.Empty, string.Empty);
        workspace.SetJobSetJobPosting(jobSet.Id, PlaywrightDemoSeedData.JobPostings[index]);
        workspace.SetJobSetAdditionalInstructions(jobSet.Id, "Keep the response concise and focus on delivery leadership evidence.");
        workspace.SetJobSetOutputLanguage(jobSet.Id, OutputLanguage.English);
        workspace.SetJobSetBatchSelection(jobSet.Id, false);
        workspace.ResetJobSetProgress(jobSet.Id);
    }

    workspace.SetDraftGenerationPreferences(new DraftGenerationPreferences
    {
        GenerateCv = false,
        GenerateCoverLetter = false,
        GenerateSummary = true,
        GenerateRecommendations = false,
        GenerateInterviewNotes = false,
        ContactEmail = "alex.taylor@example.test",
        ContactLinkedIn = "https://www.linkedin.com/in/alex-taylor-demo",
        ContactCity = "Copenhagen"
    });
}

static void SeedPlaywrightFullDemoWorkspace(WorkspaceSession workspace, JobFitWorkspaceRefreshService fitRefreshService, string selectedModel)
{
    workspace.SetApplicantDifferentiatorProfile(PlaywrightDemoSeedData.ApplicantDifferentiators);

    var seededJobSets = workspace.JobSets.OrderBy(static jobSet => jobSet.SortOrder).Take(PlaywrightDemoSeedData.JobPostings.Length).ToArray();
    for (var index = 0; index < seededJobSets.Length; index++)
    {
        var jobSet = seededJobSets[index];
        var companyProfile = PlaywrightDemoSeedData.CompanyProfiles[index];
        workspace.SetJobSetCompanyProfile(jobSet.Id, companyProfile);
        fitRefreshService.RefreshJobSet(jobSet.Id, resetSelections: true);
        workspace.SetJobSetTechnologyGapAssessment(
            jobSet.Id,
            TechnologyGapAnalyzer.Analyze(workspace.CandidateProfile, PlaywrightDemoSeedData.JobPostings[index], companyProfile));
        workspace.SetJobSetGeneratedDocuments(
            jobSet.Id,
            PlaywrightDemoSeedData.BuildGeneratedDocuments(PlaywrightDemoSeedData.JobPostings[index], selectedModel),
            PlaywrightDemoSeedData.BuildExportResults(PlaywrightDemoSeedData.JobPostings[index]));
        workspace.SetJobSetBatchSelection(jobSet.Id, false);
    }
}

public sealed record PlaywrightDemoSeedResult(string Model, IReadOnlyList<string> CompanyNames, IReadOnlyList<string> JobSetIds);

public static class PlaywrightDemoSeedData
{
    public static readonly CandidateProfile CandidateProfile = new()
    {
        Name = new PersonName("Alex", "Taylor"),
        Headline = "Senior platform and product delivery leader",
        Summary = "Leads cross-functional delivery, AI-enabled workflows, Azure modernization, and stakeholder alignment for complex product portfolios.",
        Industry = "Software and consulting",
        Location = "Copenhagen, Denmark",
        PublicProfileUrl = "https://www.linkedin.com/in/alex-taylor-demo",
        PrimaryEmail = "alex.taylor@example.test",
        Experience =
        [
            new ExperienceEntry(
                "Blue Harbor Consulting",
                "Principal Delivery Lead",
                "Led multi-team modernization programs, introduced evidence-based planning, and improved executive reporting for regulated clients.",
                "Copenhagen",
                new DateRange(new PartialDate("2021", 2021)),
                ["Reduced portfolio reporting cycle time by 40%.", "Coached product teams on outcome-based delivery and risk management."]),
            new ExperienceEntry(
                "Signal Forge Systems",
                "Solution Architect",
                "Designed Azure integration platforms and helped teams translate business goals into maintainable software systems.",
                "Remote",
                new DateRange(new PartialDate("2018", 2018), new PartialDate("2021", 2021)),
                ["Shipped a reusable integration foundation across six product teams."])
        ],
        Education =
        [
            new EducationEntry(
                "Copenhagen Business School",
                "MSc in Business Administration and Information Systems",
                "Focused on digital strategy, governance, and organizational change.",
                "Product leadership forum",
                new DateRange(new PartialDate("2014", 2014), new PartialDate("2016", 2016)))
        ],
        Skills =
        [
            new SkillTag("Azure", 1),
            new SkillTag("Product strategy", 2),
            new SkillTag("Stakeholder management", 3),
            new SkillTag("AI-assisted workflows", 4),
            new SkillTag("Agile delivery", 5),
            new SkillTag("Prompt evaluation", 6),
            new SkillTag("Executive communication", 7)
        ],
        Certifications =
        [
            new CertificationEntry(
                "Microsoft Certified: Azure Solutions Architect Expert",
                "Microsoft",
                new Uri("https://learn.microsoft.com/certifications/azure-solutions-architect/"),
                new DateRange(new PartialDate("2023", 2023)),
                null)
        ],
        Projects =
        [
            new ProjectEntry(
                "AI workflow adoption scorecard",
                "Built a lightweight measurement model that helped leadership compare adoption, quality, and risk across internal AI workflow pilots.",
                null,
                new DateRange(new PartialDate("2024", 2024)))
        ],
        Recommendations =
        [
            new RecommendationEntry(
                new PersonName("Jordan", "Lee"),
                "Blue Harbor Consulting",
                "Client Partner",
                "Alex makes ambiguous delivery problems concrete and keeps senior stakeholders aligned without losing the teams doing the work.",
                "PUBLIC",
                new PartialDate("2025", 2025))
        ],
        ManualSignals = new Dictionary<string, string>
        {
            ["Delivery philosophy"] = "Evidence first, then narrative. Alex turns stakeholder concerns into visible delivery choices.",
            ["Target roles"] = "Product delivery leadership, Azure transformation, AI workflow program ownership."
        }
    };

    public static readonly ApplicantDifferentiatorProfile ApplicantDifferentiators = new()
    {
        WorkStyle = "Structured discovery, visible trade-offs, and practical delivery checkpoints.",
        CommunicationStyle = "Calm executive summaries backed by enough detail for delivery teams to act.",
        LeadershipStyle = "Creates direction, then gives teams room to solve with clear accountability.",
        StakeholderStyle = "Turns conflicting expectations into explicit choices and decision logs.",
        Motivators = "Meaningful product outcomes, responsible AI adoption, and measurable team learning.",
        TargetNarrative = "A delivery leader who bridges product ambition, technical architecture, and adoption habits.",
        Watchouts = "Avoid over-indexing on pure program management roles without product or technical influence.",
        AboutApplicantBasis = "Use LinkedIn experience, delivery metrics, Azure architecture work, and AI workflow adoption examples."
    };

    public static readonly JobPostingAnalysis[] JobPostings =
    [
        new()
        {
            RoleTitle = "Senior Product Delivery Lead",
            CompanyName = "Northwind Demo Labs",
            Summary = "Lead cross-functional product delivery for AI-enabled workflow products used by enterprise teams.",
            SourceUrl = new Uri("https://jobs.example.test/northwind-product-delivery-lead"),
            MustHaveThemes = ["Product delivery leadership", "Stakeholder alignment", "AI-enabled workflow delivery"],
            NiceToHaveThemes = ["Azure platform experience", "Portfolio reporting"],
            CulturalSignals = ["Pragmatic collaboration", "Evidence-based decision making"],
            Signals =
            [
                new JobContextSignal("Delivery", "Product delivery leadership", JobRequirementImportance.MustHave, "Job posting", "Lead cross-functional product delivery for AI-enabled workflow products.", 94, ["product delivery", "cross-functional delivery"]),
                new JobContextSignal("Technology", "AI-enabled workflow delivery", JobRequirementImportance.MustHave, "Job posting", "Products include AI-enabled workflows used by enterprise teams.", 91, ["AI workflows", "AI-enabled workflows"]),
                new JobContextSignal("Technology", "Azure platform experience", JobRequirementImportance.NiceToHave, "Job posting", "Azure platform experience is listed as a helpful background.", 80, ["Azure"])
            ],
            InferredRequirements = ["Can translate ambiguous goals into executable plans"]
        },
        new()
        {
            RoleTitle = "Azure Transformation Manager",
            CompanyName = "Fabrikam Demo Works",
            Summary = "Own delivery governance and technical coordination for Azure modernization initiatives.",
            SourceUrl = new Uri("https://jobs.example.test/fabrikam-azure-transformation-manager"),
            MustHaveThemes = ["Azure modernization", "Delivery governance", "Executive communication"],
            NiceToHaveThemes = ["Consulting background", "Risk management"],
            CulturalSignals = ["Clear communication", "Ownership mindset"],
            Signals =
            [
                new JobContextSignal("Technology", "Azure modernization", JobRequirementImportance.MustHave, "Job posting", "Own delivery governance for Azure modernization initiatives.", 95, ["Azure", "cloud modernization"]),
                new JobContextSignal("Leadership", "Executive communication", JobRequirementImportance.MustHave, "Job posting", "Coordinate between leadership, architecture, and delivery teams.", 87, ["executive communication", "stakeholder alignment"]),
                new JobContextSignal("Delivery", "Risk management", JobRequirementImportance.NiceToHave, "Job posting", "Modernization portfolio needs active risk management.", 76, ["risk management"])
            ],
            InferredRequirements = ["Can bridge architecture and delivery teams"]
        },
        new()
        {
            RoleTitle = "AI Workflow Program Lead",
            CompanyName = "Contoso Demo Group",
            Summary = "Coordinate adoption of AI-assisted internal workflows while managing change and measurable outcomes.",
            SourceUrl = new Uri("https://jobs.example.test/contoso-ai-workflow-program-lead"),
            MustHaveThemes = ["AI-assisted workflows", "Change management", "Outcome measurement"],
            NiceToHaveThemes = ["Prompt evaluation", "Internal enablement"],
            CulturalSignals = ["Curiosity", "Responsible experimentation"],
            Signals =
            [
                new JobContextSignal("Technology", "AI-assisted workflows", JobRequirementImportance.MustHave, "Job posting", "Coordinate adoption of AI-assisted internal workflows.", 96, ["AI workflows", "AI-assisted workflows"]),
                new JobContextSignal("Delivery", "Outcome measurement", JobRequirementImportance.MustHave, "Job posting", "Manage measurable outcomes for new ways of working.", 88, ["measurement", "scorecard"]),
                new JobContextSignal("Technology", "Prompt evaluation", JobRequirementImportance.NiceToHave, "Job posting", "Prompt evaluation is useful for internal enablement.", 82, ["prompt evaluation"])
            ],
            InferredRequirements = ["Can create adoption paths for new ways of working"]
        }
    ];

    public static readonly CompanyResearchProfile[] CompanyProfiles =
    [
        new()
        {
            Name = "Northwind Demo Labs",
            Summary = "Northwind Demo Labs builds workflow products for enterprise operations teams that need clear governance and practical automation.",
            SourceUrls = [new Uri("https://companies.example.test/northwind-demo-labs")],
            GuidingPrinciples = ["Make AI behavior visible", "Prefer measurable delivery outcomes"],
            CulturalSignals = ["Pragmatic collaboration", "Evidence-based decision making"],
            Differentiators = ["Combines workflow automation with adoption coaching", "Ships operational scorecards with product changes"],
            Signals =
            [
                new JobContextSignal("Company", "Enterprise workflow adoption", JobRequirementImportance.MustHave, "Company profile", "Customers expect adoption planning with every workflow launch.", 86, ["workflow adoption", "adoption planning"])
            ]
        },
        new()
        {
            Name = "Fabrikam Demo Works",
            Summary = "Fabrikam Demo Works helps regulated teams modernize Azure platforms while keeping executive governance and delivery risk visible.",
            SourceUrls = [new Uri("https://companies.example.test/fabrikam-demo-works")],
            GuidingPrinciples = ["Governance belongs in the delivery rhythm", "Architecture choices must be explainable"],
            CulturalSignals = ["Ownership mindset", "Clear communication"],
            Differentiators = ["Modernization playbooks for multi-team portfolios", "Strong executive reporting cadence"],
            Signals =
            [
                new JobContextSignal("Company", "Azure delivery governance", JobRequirementImportance.MustHave, "Company profile", "Modernization programs use shared Azure governance patterns.", 90, ["Azure governance", "delivery governance"])
            ]
        },
        new()
        {
            Name = "Contoso Demo Group",
            Summary = "Contoso Demo Group introduces AI-assisted internal tooling with a strong focus on responsible experimentation and measurable enablement.",
            SourceUrls = [new Uri("https://companies.example.test/contoso-demo-group")],
            GuidingPrinciples = ["Adoption before automation", "Measure quality and confidence together"],
            CulturalSignals = ["Curiosity", "Responsible experimentation"],
            Differentiators = ["Internal AI enablement community", "Prompt evaluation practice for operational teams"],
            Signals =
            [
                new JobContextSignal("Company", "Responsible AI enablement", JobRequirementImportance.MustHave, "Company profile", "Teams need enablement paths for safe AI workflow adoption.", 92, ["responsible AI", "AI enablement"])
            ]
        }
    ];

    public static readonly string[] CompanyNames = JobPostings
        .Select(static posting => posting.CompanyName)
        .Concat(CandidateProfile.Experience.Select(static entry => entry.CompanyName))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static IReadOnlyList<GeneratedDocument> BuildGeneratedDocuments(JobPostingAnalysis posting, string selectedModel)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        return
        [
            new GeneratedDocument(
                DocumentKind.ProfileSummary,
                $"Targeted profile summary - {posting.RoleTitle}",
                $"# Targeted profile summary\n\nAlex Taylor is positioned for **{posting.RoleTitle}** by combining product delivery leadership, Azure modernization, and measurable AI workflow adoption. The narrative emphasizes stakeholder alignment, evidence-based delivery, and practical change management for {posting.CompanyName}.",
                $"Alex Taylor is positioned for {posting.RoleTitle} through product delivery, Azure modernization, and AI workflow adoption.",
                generatedAt,
                LlmDuration: TimeSpan.FromSeconds(22),
                PromptTokens: 1_240,
                CompletionTokens: 420,
                Model: selectedModel),
            new GeneratedDocument(
                DocumentKind.InterviewNotes,
                $"Interview prep - {posting.RoleTitle}",
                $"# Interview prep\n\n- Lead with the delivery scorecard example.\n- Connect Azure architecture work to portfolio governance.\n- Explain how AI workflow adoption was measured and coached.\n- Ask how {posting.CompanyName} defines responsible experimentation.",
                $"Interview prep for {posting.RoleTitle}.",
                generatedAt,
                LlmDuration: TimeSpan.FromSeconds(18),
                PromptTokens: 980,
                CompletionTokens: 310,
                Model: selectedModel)
        ];
    }

    public static IReadOnlyList<DocumentExportResult> BuildExportResults(JobPostingAnalysis posting)
    {
        var slug = string.Join('-', posting.RoleTitle.ToLowerInvariant().Split([' ', '/', '\\'], StringSplitOptions.RemoveEmptyEntries));
        return
        [
            new DocumentExportResult(DocumentKind.ProfileSummary, Path.Combine("artifacts", "playwright", "full-app-demo-exports", $"{slug}-summary.docx")),
            new DocumentExportResult(DocumentKind.InterviewNotes, Path.Combine("artifacts", "playwright", "full-app-demo-exports", $"{slug}-interview-notes.docx"))
        ];
    }
}

