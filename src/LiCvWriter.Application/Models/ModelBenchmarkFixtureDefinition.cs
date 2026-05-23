namespace LiCvWriter.Application.Models;

/// <summary>
/// Deterministic benchmark fixture definition executed against a local model.
/// </summary>
public sealed record ModelBenchmarkFixtureDefinition(
    string FixtureId,
    string PromptId,
    string DisplayName,
    double Weight,
    string SystemPrompt,
    string UserPrompt,
    LlmResponseFormat ResponseFormat);