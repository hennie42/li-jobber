using LiCvWriter.Application.Models;

namespace LiCvWriter.Application.Abstractions;

/// <summary>
/// Lists and downloads models from the Microsoft Foundry Local catalog.
/// </summary>
public interface IFoundryCatalogClient
{
    /// <summary>
    /// Reads the current catalog state, including cached and loaded model aliases and Windows acceleration diagnostics.
    /// </summary>
    Task<FoundryCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and registers Windows ML execution providers through Foundry Local.
    /// </summary>
    Task<FoundryAccelerationSnapshot> RegisterExecutionProvidersAsync(
        IReadOnlyList<string>? names = null,
        Action<string, double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the requested model alias into the local Foundry cache.
    /// </summary>
    Task DownloadModelAsync(
        string alias,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the requested model alias from the local Foundry cache.
    /// </summary>
    Task RemoveModelAsync(
        string alias,
        CancellationToken cancellationToken = default);
}