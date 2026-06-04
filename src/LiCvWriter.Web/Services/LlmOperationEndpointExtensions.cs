using System.Text.Json;
using LiCvWriter.Application.Models;

namespace LiCvWriter.Web.Services;

/// <summary>
/// Maps LLM operation start, status, cancellation, and event-stream endpoints.
/// </summary>
internal static class LlmOperationEndpointExtensions
{
    public static IEndpointRouteBuilder MapLiCvWriterLlmOperationEndpoints(this IEndpointRouteBuilder app)
    {
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

        return app;
    }
}