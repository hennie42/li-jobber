namespace LiCvWriter.Application.Models;

public sealed record LlmProgressUpdate(
    string Message,
    string? Detail,
    string Model,
    TimeSpan? Elapsed = null,
    bool Completed = false,
    long? PromptTokens = null,
    long? CompletionTokens = null,
    TimeSpan? EstimatedRemaining = null,
    string? ThinkingPreview = null);