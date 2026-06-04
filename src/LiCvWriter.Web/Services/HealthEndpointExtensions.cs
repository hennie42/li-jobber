using LiCvWriter.Application.Abstractions;
using LiCvWriter.Infrastructure.Llm;

namespace LiCvWriter.Web.Services;

/// <summary>
/// Maps health endpoints for local LLM provider availability checks.
/// </summary>
internal static class HealthEndpointExtensions
{
    public static IEndpointRouteBuilder MapLiCvWriterHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health/ollama", async (OllamaClient client, CancellationToken cancellationToken) =>
        {
            var result = await client.VerifyModelAvailabilityAsync(cancellationToken);
            return Results.Ok(result);
        });

        app.MapGet("/api/health/foundry", async (IFoundryCatalogClient catalogClient, CancellationToken cancellationToken) =>
        {
            var result = await catalogClient.GetSnapshotAsync(cancellationToken);
            return Results.Ok(result.Availability);
        });

        app.MapGet("/api/health/foundry/acceleration", async (IFoundryCatalogClient catalogClient, CancellationToken cancellationToken) =>
        {
            var result = await catalogClient.GetSnapshotAsync(cancellationToken);
            return Results.Ok(result.Acceleration);
        });

        return app;
    }
}