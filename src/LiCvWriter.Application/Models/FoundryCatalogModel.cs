namespace LiCvWriter.Application.Models;

/// <summary>
/// Represents a Foundry Local catalog entry as surfaced to the setup UI.
/// </summary>
public sealed record FoundryCatalogModel(
    string Alias,
    string DisplayName,
    string ModelId,
    int? FileSizeMb,
    bool IsCached,
    bool IsLoaded,
    string? Description = null,
    bool IsTextBenchmarkUsable = true,
    string? TextBenchmarkUnusableReason = null);