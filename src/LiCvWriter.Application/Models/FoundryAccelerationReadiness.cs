namespace LiCvWriter.Application.Models;

/// <summary>
/// Describes how ready the current Foundry Local runtime is to use the best Windows ML acceleration path.
/// </summary>
public enum FoundryAccelerationReadiness
{
    Unsupported,
    Disabled,
    Unavailable,
    NeedsRegistration,
    PartiallyReady,
    Ready
}