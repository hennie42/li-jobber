using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;

namespace LiCvWriter.Infrastructure.Llm;

/// <summary>
/// Executes chat completions against Microsoft Foundry Local models that have already been downloaded.
/// </summary>
public sealed class FoundryLlmClient(IFoundrySdkBridge bridge) : ILlmClient
{
    public async Task<LlmModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
        => await bridge.VerifyModelAvailabilityAsync(cancellationToken);

    public async Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
        => await bridge.GetModelInfoAsync(model, cancellationToken);

    public async Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        Action<LlmProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
        => await bridge.GenerateAsync(request, progress, cancellationToken);
}