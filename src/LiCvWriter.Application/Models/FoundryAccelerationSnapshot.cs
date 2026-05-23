namespace LiCvWriter.Application.Models;

/// <summary>
/// Captures Windows-only Foundry acceleration capabilities such as execution-provider discovery.
/// </summary>
public sealed record FoundryAccelerationSnapshot(
    bool IsSupported,
    bool IsEnabled,
    bool CanManageExecutionProviders,
    string StatusMessage,
    IReadOnlyList<FoundryExecutionProviderInfo> ExecutionProviders,
    DateTimeOffset CollectedAtUtc)
{
    public static FoundryAccelerationSnapshot Unsupported(string statusMessage)
        => new(false, false, false, statusMessage, [], DateTimeOffset.UtcNow);

    public static FoundryAccelerationSnapshot Disabled(string statusMessage)
        => new(true, false, false, statusMessage, [], DateTimeOffset.UtcNow);

    public static FoundryAccelerationSnapshot Unavailable(string statusMessage)
        => new(true, true, false, statusMessage, [], DateTimeOffset.UtcNow);
}