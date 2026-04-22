namespace LiCvWriter.Application.Models;

/// <summary>
/// Static metadata about an installed Ollama model, sourced from <c>/api/tags</c>
/// and <c>/api/show</c>. Used by the capacity probe to reason about whether a
/// model is realistically runnable on the current hardware before any inference
/// is attempted.
/// </summary>
public sealed record OllamaModelInfo(
    string Name,
    long? FileSizeBytes,
    string? ParameterSize,
    string? QuantizationLevel,
    string? Family,
    long? ContextLength);
