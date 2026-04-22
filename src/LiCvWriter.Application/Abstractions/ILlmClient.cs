using LiCvWriter.Application.Models;

namespace LiCvWriter.Application.Abstractions;

public interface ILlmClient
{
    Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        Action<LlmProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns rich metadata for an installed model (parameter size, quantization,
    /// context length, family, file size). Returns <c>null</c> when the model is
    /// not installed or the metadata endpoint is unavailable.
    /// </summary>
    Task<OllamaModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default);
}
