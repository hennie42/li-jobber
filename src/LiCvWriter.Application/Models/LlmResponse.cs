namespace LiCvWriter.Application.Models;

public sealed record LlmResponse(
    string Model,
    string Content,
    string? Thinking,
    bool Completed,
    long? PromptTokens,
    long? CompletionTokens,
    TimeSpan? Duration);
