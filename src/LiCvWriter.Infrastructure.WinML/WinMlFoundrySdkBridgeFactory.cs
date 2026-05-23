using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Options;

namespace LiCvWriter.Infrastructure.WinML;

/// <summary>
/// Creates the Windows-only Foundry bridge backed by the WinML package variant.
/// </summary>
public sealed class WinMlFoundrySdkBridgeFactory : IFoundrySdkBridgeFactory
{
    public IFoundrySdkBridge CreateBridge(FoundryOptions options)
        => new WinMlFoundrySdkBridge(options);
}