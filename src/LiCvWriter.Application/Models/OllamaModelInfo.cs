namespace LiCvWriter.Application.Models;

/// <summary>
/// Static metadata about an installed local model, used to reason about whether a
/// model is realistically runnable on the current hardware before any inference
/// is attempted.
/// </summary>
public sealed record LlmModelInfo(
    string Name,
    long? FileSizeBytes,
    string? ParameterSize,
    string? QuantizationLevel,
    string? Family,
    long? ContextLength,
    LlmProviderKind Provider = LlmProviderKind.Ollama);
