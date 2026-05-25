using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;

namespace LiCvWriter.Infrastructure.Llm;

internal sealed class UnavailableFoundrySdkBridge(string message) : IFoundrySdkBridge
{
    public Task<FoundryCatalogSnapshot> GetCatalogSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromException<FoundryCatalogSnapshot>(BuildException());

    public Task<FoundryAccelerationSnapshot> RegisterExecutionProvidersAsync(
        IReadOnlyList<string>? names = null,
        Action<string, double>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.FromException<FoundryAccelerationSnapshot>(BuildException());

    public Task DownloadModelAsync(
        string alias,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.FromException(BuildException());

    public Task UnloadModelAsync(
        string alias,
        CancellationToken cancellationToken = default)
        => Task.FromException(BuildException());

    public Task RemoveModelAsync(
        string alias,
        CancellationToken cancellationToken = default)
        => Task.FromException(BuildException());

    public Task<LlmModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
        => Task.FromException<LlmModelAvailability>(BuildException());

    public Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
        => Task.FromException<LlmModelInfo?>(BuildException());

    public Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        Action<LlmProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.FromException<LlmResponse>(BuildException());

    private InvalidOperationException BuildException()
        => new(message);
}