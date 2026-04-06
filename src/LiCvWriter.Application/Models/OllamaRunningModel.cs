namespace LiCvWriter.Application.Models;

public sealed record OllamaRunningModel(
    string Name,
    string Model,
    DateTimeOffset? ExpiresAtUtc,
    long? SizeVramBytes);