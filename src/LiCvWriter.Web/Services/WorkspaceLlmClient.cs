using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Infrastructure.Llm;

namespace LiCvWriter.Web.Services;

/// <summary>
/// Routes LLM calls to the provider currently selected in the workspace session.
/// </summary>
public sealed class WorkspaceLlmClient(
    WorkspaceSession workspace,
    OllamaClient ollamaClient,
    FoundryLlmClient foundryClient) : ILlmClient
{
    public Task<LlmModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
        => ResolveClient().VerifyModelAvailabilityAsync(cancellationToken);

    public Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        Action<LlmProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
        => ResolveClient().GenerateAsync(request, progress, cancellationToken);

    public Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
        => ResolveClient().GetModelInfoAsync(model, cancellationToken);

    private ILlmClient ResolveClient()
        => workspace.SelectedLlmProvider switch
        {
            LlmProviderKind.Foundry => foundryClient,
            _ => ollamaClient
        };
}