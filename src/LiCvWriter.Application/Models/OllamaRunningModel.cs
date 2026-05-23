namespace LiCvWriter.Application.Models;

public sealed record LlmRunningModel(
    string Name,
    string Model,
    DateTimeOffset? ExpiresAtUtc,
    long? SizeVramBytes,
    long? SizeBytes = null,
    LlmProviderKind Provider = LlmProviderKind.Ollama);