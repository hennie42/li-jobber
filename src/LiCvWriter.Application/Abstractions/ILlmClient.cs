using LiCvWriter.Application.Models;

namespace LiCvWriter.Application.Abstractions;

public interface ILlmClient
{
    Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        Action<LlmProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);
}
