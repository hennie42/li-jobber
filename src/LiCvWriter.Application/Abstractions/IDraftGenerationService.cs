using LiCvWriter.Application.Models;

namespace LiCvWriter.Application.Abstractions;

public interface IDraftGenerationService
{
    Task<DraftGenerationResult> GenerateAsync(
        DraftGenerationRequest request,
        Action<LlmProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);
}
