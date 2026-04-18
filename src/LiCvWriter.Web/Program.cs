using System.Net;
using System.Text.Json;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using LiCvWriter.Core.Jobs;
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

builder.Configuration.AddUserSecrets<Program>(optional: true);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var linkedInOptions = builder.Configuration.GetSection(LinkedInAuthOptions.SectionName).Get<LinkedInAuthOptions>() ?? new LinkedInAuthOptions();
var ollamaOptions = builder.Configuration.GetSection(OllamaOptions.SectionName).Get<OllamaOptions>() ?? new OllamaOptions();
var storageOptions = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();

builder.Services.AddSingleton(linkedInOptions);
builder.Services.AddSingleton(ollamaOptions);
builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton<SimpleCsvParser>();
builder.Services.AddSingleton<LinkedInPartialDateParser>();
builder.Services.AddSingleton<CandidateProfileMergeService>();
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
builder.Services.AddSingleton<IDocumentExportService, LocalDocumentExportService>();
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

builder.Services.AddHttpClient<IJobResearchService, HttpJobResearchService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(1);
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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

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

