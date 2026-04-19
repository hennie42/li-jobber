namespace LiCvWriter.Application.Models;

public sealed record LlmRequest(
    string Model,
    string? SystemPrompt,
    IReadOnlyList<LlmChatMessage> Messages,
    bool UseChatEndpoint = true,
    bool Stream = false,
    string? Think = null,
    string? KeepAlive = null,
    double? Temperature = null,
    int? NumPredict = null);
