using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;

namespace LiCvWriter.Infrastructure.Llm;

/// <summary>
/// Adapts the Foundry Local SDK catalog into UI-friendly model metadata and download operations.
/// </summary>
public sealed class FoundryCatalogClient(IFoundrySdkBridge bridge) : IFoundryCatalogClient
{
    public async Task<FoundryCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => await bridge.GetCatalogSnapshotAsync(cancellationToken);

    public async Task<FoundryAccelerationSnapshot> RegisterExecutionProvidersAsync(
        IReadOnlyList<string>? names = null,
        Action<string, double>? progress = null,
        CancellationToken cancellationToken = default)
        => await bridge.RegisterExecutionProvidersAsync(names, progress, cancellationToken);

    public async Task DownloadModelAsync(
        string alias,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default)
        => await bridge.DownloadModelAsync(alias, progress, cancellationToken);

    public async Task UnloadModelAsync(
        string alias,
        CancellationToken cancellationToken = default)
        => await bridge.UnloadModelAsync(alias, cancellationToken);

    public async Task RemoveModelAsync(
        string alias,
        CancellationToken cancellationToken = default)
        => await bridge.RemoveModelAsync(alias, cancellationToken);
}