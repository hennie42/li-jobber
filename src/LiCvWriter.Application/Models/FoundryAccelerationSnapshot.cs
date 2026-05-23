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

    /// <summary>
    /// Gets how ready the runtime is to use the strongest visible Windows ML hardware path.
    /// </summary>
    public FoundryAccelerationReadiness Readiness
        => this switch
        {
            { IsSupported: false } => FoundryAccelerationReadiness.Unsupported,
            { IsEnabled: false } => FoundryAccelerationReadiness.Disabled,
            { CanManageExecutionProviders: false } => FoundryAccelerationReadiness.Unavailable,
            _ when RegisteredExecutionProviderCount == 0 => FoundryAccelerationReadiness.NeedsRegistration,
            _ when RegisteredExecutionProviderCount < DiscoveredExecutionProviderCount => FoundryAccelerationReadiness.PartiallyReady,
            _ => FoundryAccelerationReadiness.Ready
        };

    /// <summary>
    /// Gets the number of execution providers that the runtime reported.
    /// </summary>
    public int DiscoveredExecutionProviderCount => ExecutionProviders.Count;

    /// <summary>
    /// Gets the number of execution providers that are already registered.
    /// </summary>
    public int RegisteredExecutionProviderCount
        => ExecutionProviders.Count(static executionProvider => executionProvider.IsRegistered);

    /// <summary>
    /// Gets user-facing guidance for interpreting the current acceleration readiness.
    /// </summary>
    public string GuidanceMessage
        => Readiness switch
        {
            FoundryAccelerationReadiness.Unsupported => "Windows ML acceleration diagnostics are only available on Windows. This runtime can still use Foundry Local, but it cannot confirm the WinML hardware path.",
            FoundryAccelerationReadiness.Disabled => "Windows ML acceleration is disabled in configuration, so Foundry benchmarks will not reflect the strongest available WinML path.",
            FoundryAccelerationReadiness.Unavailable => $"{StatusMessage} This runtime path cannot confirm whether Foundry is using the strongest hardware route.",
            FoundryAccelerationReadiness.NeedsRegistration when DiscoveredExecutionProviderCount == 0 => "No execution providers are reported by the installed Foundry runtime. Expect generic local-runtime behavior until the WinML path is available.",
            FoundryAccelerationReadiness.NeedsRegistration => "Execution providers are discovered but none are registered yet. Foundry can fall back to a weaker CPU-style path until registration completes.",
            FoundryAccelerationReadiness.PartiallyReady => $"{RegisteredExecutionProviderCount} of {DiscoveredExecutionProviderCount} execution provider(s) are registered. Foundry is usable now, but hardware-path performance can still improve after registering the remaining providers.",
            _ => $"{RegisteredExecutionProviderCount} execution provider(s) are registered. Benchmarks should reflect the best compatible hardware path the local runtime can actually use on this machine."
        };
}