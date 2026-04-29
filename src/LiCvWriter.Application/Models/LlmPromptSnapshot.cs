namespace LiCvWriter.Application.Models;

/// <summary>
/// Captures the verbatim system and user prompts sent to the LLM for a single LLM call.
/// Stored in memory for session-level inspection.
/// </summary>
public sealed record LlmPromptSnapshot(
    string OperationLabel,
    string SystemPrompt,
    string UserPrompt,
    DateTimeOffset CapturedAtUtc,
    string? PromptId = null,
    string? PromptVersion = null);
