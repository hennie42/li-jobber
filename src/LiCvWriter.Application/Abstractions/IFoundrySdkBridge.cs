using LiCvWriter.Application.Models;

namespace LiCvWriter.Application.Abstractions;

/// <summary>
/// Bridges Foundry SDK operations into the application's stable model surface.
/// </summary>
public interface IFoundrySdkBridge
{
    Task<FoundryCatalogSnapshot> GetCatalogSnapshotAsync(CancellationToken cancellationToken = default);

    Task<FoundryAccelerationSnapshot> RegisterExecutionProvidersAsync(
        IReadOnlyList<string>? names = null,
        Action<string, double>? progress = null,
        CancellationToken cancellationToken = default);

    Task DownloadModelAsync(
        string alias,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task UnloadModelAsync(
        string alias,
        CancellationToken cancellationToken = default);

    Task RemoveModelAsync(
        string alias,
        CancellationToken cancellationToken = default);

    Task<LlmModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default);

    Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        Action<LlmProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);
}