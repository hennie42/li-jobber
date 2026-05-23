namespace LiCvWriter.Application.Models;

/// <summary>
/// Captures the current Foundry Local catalog together with the locally cached and loaded state.
/// </summary>
public sealed record FoundryCatalogSnapshot(
    LlmModelAvailability Availability,
    IReadOnlyList<FoundryCatalogModel> Models,
    FoundryAccelerationSnapshot Acceleration,
    DateTimeOffset CollectedAtUtc);