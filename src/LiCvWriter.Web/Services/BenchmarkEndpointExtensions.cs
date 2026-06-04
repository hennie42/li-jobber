using LiCvWriter.Application.Models;

namespace LiCvWriter.Web.Services;

/// <summary>
/// Maps model benchmark queue endpoints.
/// </summary>
internal static class BenchmarkEndpointExtensions
{
    public static IEndpointRouteBuilder MapLiCvWriterBenchmarkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/benchmarks/model-queue", (StartModelBenchmarkRequest request, ModelBenchmarkCoordinator coordinator) =>
        {
            try
            {
                _ = coordinator.StartAsync(
                    request.Provider,
                    request.Models,
                    request.DownloadMissingModels,
                    request.RemoveTooLargeModelsAfterBenchmark);

                return Results.Accepted("/api/benchmarks/model-queue", coordinator.Current ?? coordinator.Last);
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(exception.Message);
            }
        });

        app.MapGet("/api/benchmarks/model-queue", (ModelBenchmarkCoordinator coordinator) =>
        {
            var session = coordinator.Current ?? coordinator.Last;
            return session is null ? Results.NotFound() : Results.Ok(session);
        });

        app.MapPost("/api/benchmarks/model-queue/cancel", (ModelBenchmarkCoordinator coordinator) =>
        {
            if (!coordinator.IsRunning)
            {
                return Results.NotFound();
            }

            coordinator.Cancel();
            return Results.Accepted("/api/benchmarks/model-queue", coordinator.Current ?? coordinator.Last);
        });

        return app;
    }
}