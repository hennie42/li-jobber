using LiCvWriter.Application.Models;

namespace LiCvWriter.Tests.Application;

public sealed class FoundryAccelerationSnapshotTests
{
    [Fact]
    public void Readiness_WhenRuntimeIsUnsupported_ReturnsUnsupported()
    {
        var snapshot = FoundryAccelerationSnapshot.Unsupported("Windows ML acceleration is only available on Windows.");

        Assert.Equal(FoundryAccelerationReadiness.Unsupported, snapshot.Readiness);
        Assert.Contains("only available on Windows", snapshot.GuidanceMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Readiness_WhenProvidersNeedRegistration_ReturnsNeedsRegistration()
    {
        var snapshot = new FoundryAccelerationSnapshot(
            IsSupported: true,
            IsEnabled: true,
            CanManageExecutionProviders: true,
            StatusMessage: "Foundry Local discovered 2 Windows ML execution provider(s), but none are registered yet.",
            ExecutionProviders:
            [
                new FoundryExecutionProviderInfo("dml", "DirectML", false),
                new FoundryExecutionProviderInfo("cuda", "CUDA", false)
            ],
            CollectedAtUtc: DateTimeOffset.UtcNow);

        Assert.Equal(FoundryAccelerationReadiness.NeedsRegistration, snapshot.Readiness);
        Assert.Equal(0, snapshot.RegisteredExecutionProviderCount);
        Assert.Equal(2, snapshot.DiscoveredExecutionProviderCount);
        Assert.Contains("none are registered yet", snapshot.GuidanceMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Readiness_WhenSomeProvidersAreRegistered_ReturnsPartiallyReady()
    {
        var snapshot = new FoundryAccelerationSnapshot(
            IsSupported: true,
            IsEnabled: true,
            CanManageExecutionProviders: true,
            StatusMessage: "Foundry Local reports 1/2 Windows ML execution provider(s) registered.",
            ExecutionProviders:
            [
                new FoundryExecutionProviderInfo("dml", "DirectML", true),
                new FoundryExecutionProviderInfo("cuda", "CUDA", false)
            ],
            CollectedAtUtc: DateTimeOffset.UtcNow);

        Assert.Equal(FoundryAccelerationReadiness.PartiallyReady, snapshot.Readiness);
        Assert.Contains("can still improve", snapshot.GuidanceMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Readiness_WhenAllProvidersAreRegistered_ReturnsReady()
    {
        var snapshot = new FoundryAccelerationSnapshot(
            IsSupported: true,
            IsEnabled: true,
            CanManageExecutionProviders: true,
            StatusMessage: "Foundry Local reports all 2 Windows ML execution provider(s) registered.",
            ExecutionProviders:
            [
                new FoundryExecutionProviderInfo("dml", "DirectML", true),
                new FoundryExecutionProviderInfo("cuda", "CUDA", true)
            ],
            CollectedAtUtc: DateTimeOffset.UtcNow);

        Assert.Equal(FoundryAccelerationReadiness.Ready, snapshot.Readiness);
        Assert.Contains("best compatible hardware path", snapshot.GuidanceMessage, StringComparison.OrdinalIgnoreCase);
    }
}