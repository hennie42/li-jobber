namespace LiCvWriter.Application.Models;

/// <summary>
/// Represents a discovered Windows ML execution provider surfaced through Foundry Local.
/// </summary>
public sealed record FoundryExecutionProviderInfo(
    string Name,
    string DisplayName,
    bool IsRegistered);