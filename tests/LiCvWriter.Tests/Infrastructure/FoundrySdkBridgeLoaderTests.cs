using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Infrastructure.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class FoundrySdkBridgeLoaderTests
{
    [Fact]
    public void Create_WhenNotRunningOnWindows_ReturnsFallbackBridge()
    {
        var fallbackBridge = new StubFoundrySdkBridge();

        var bridge = FoundrySdkBridgeLoader.Create(
            new FoundryOptions(),
            NullLoggerFactory.Instance,
            fallbackBridge,
            isWindows: false,
            pluginAssemblyPath: Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dll"));

        Assert.Same(fallbackBridge, bridge);
    }

    [Fact]
    public async Task Create_WhenPluginIsMissingOnWindows_ReturnsFriendlyFailureBridge()
    {
        var bridge = FoundrySdkBridgeLoader.Create(
            new FoundryOptions(),
            NullLoggerFactory.Instance,
            new StubFoundrySdkBridge(),
            isWindows: true,
            pluginAssemblyPath: Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dll"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.VerifyModelAvailabilityAsync());

        Assert.Contains("WinML adapter", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubFoundrySdkBridge : IFoundrySdkBridge
    {
        public Task<FoundryCatalogSnapshot> GetCatalogSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new FoundryCatalogSnapshot(
                new LlmModelAvailability(string.Empty, string.Empty, false, Array.Empty<string>(), Provider: LlmProviderKind.Foundry),
                Array.Empty<FoundryCatalogModel>(),
                FoundryAccelerationSnapshot.Unsupported("not used"),
                DateTimeOffset.UtcNow));

        public Task<FoundryAccelerationSnapshot> RegisterExecutionProvidersAsync(
            IReadOnlyList<string>? names = null,
            Action<string, double>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(FoundryAccelerationSnapshot.Unsupported("not used"));

        public Task DownloadModelAsync(
            string alias,
            Action<double>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveModelAsync(
            string alias,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<LlmModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmModelAvailability(string.Empty, string.Empty, false, Array.Empty<string>(), Provider: LlmProviderKind.Foundry));

        public Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => Task.FromResult<LlmModelInfo?>(null);

        public Task<LlmResponse> GenerateAsync(
            LlmRequest request,
            Action<LlmProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmResponse(request.Model, string.Empty, null, true, null, null, null));
    }
}