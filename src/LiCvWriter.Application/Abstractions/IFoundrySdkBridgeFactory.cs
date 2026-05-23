using LiCvWriter.Application.Options;

namespace LiCvWriter.Application.Abstractions;

/// <summary>
/// Creates a platform-specific Foundry SDK bridge.
/// </summary>
public interface IFoundrySdkBridgeFactory
{
    IFoundrySdkBridge CreateBridge(FoundryOptions options);
}