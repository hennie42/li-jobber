using System.Reflection;
using System.Runtime.Loader;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using Microsoft.Extensions.Logging;

namespace LiCvWriter.Infrastructure.Llm;

public static class FoundrySdkBridgeLoader
{
    internal const string PluginAssemblyFileName = "LiCvWriter.Infrastructure.WinML.dll";
    private const string PluginFactoryTypeName = "LiCvWriter.Infrastructure.WinML.WinMlFoundrySdkBridgeFactory";

    public static IFoundrySdkBridge Create(
        FoundryOptions options,
        ILoggerFactory loggerFactory,
        IFoundrySdkBridge fallbackBridge,
        bool? isWindows = null,
        string? pluginAssemblyPath = null)
    {
        var runningOnWindows = isWindows ?? OperatingSystem.IsWindows();
        if (!runningOnWindows)
        {
            return fallbackBridge;
        }

        var resolvedPluginPath = pluginAssemblyPath ?? Path.Combine(AppContext.BaseDirectory, PluginAssemblyFileName);
        if (!File.Exists(resolvedPluginPath))
        {
            return new UnavailableFoundrySdkBridge(
                $"Microsoft Foundry Local for Windows is unavailable because {PluginAssemblyFileName} was not found beside the application binaries. Build the Windows-only WinML adapter and rerun the app on Windows.");
        }

        try
        {
            var loadContext = new PluginLoadContext(resolvedPluginPath);
            var assembly = loadContext.LoadFromAssemblyPath(resolvedPluginPath);
            var factoryType = assembly.GetType(PluginFactoryTypeName, throwOnError: false, ignoreCase: false);

            if (factoryType is null
                || factoryType.IsAbstract
                || !typeof(IFoundrySdkBridgeFactory).IsAssignableFrom(factoryType)
                || factoryType.GetConstructor(Type.EmptyTypes) is null)
            {
                return new UnavailableFoundrySdkBridge(
                    $"Microsoft Foundry Local for Windows is unavailable because {PluginAssemblyFileName} does not expose an {nameof(IFoundrySdkBridgeFactory)} implementation.");
            }

            var factory = Activator.CreateInstance(factoryType) as IFoundrySdkBridgeFactory;
            if (factory is null)
            {
                return new UnavailableFoundrySdkBridge(
                    $"Microsoft Foundry Local for Windows is unavailable because {PluginAssemblyFileName} could not create its bridge factory.");
            }

            return new LoadedFoundrySdkBridge(loadContext, factory.CreateBridge(options));
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("FoundryLocal").LogWarning(exception, "Failed to load the Windows-only Foundry WinML bridge.");
            return new UnavailableFoundrySdkBridge(
                $"Microsoft Foundry Local for Windows could not load the WinML adapter from '{resolvedPluginPath}'. {exception.Message}");
        }
    }

    private sealed class LoadedFoundrySdkBridge(PluginLoadContext loadContext, IFoundrySdkBridge innerBridge) : IFoundrySdkBridge, IDisposable
    {
        private readonly PluginLoadContext loadContext = loadContext;
        private readonly IFoundrySdkBridge innerBridge = innerBridge;

        public Task<FoundryCatalogSnapshot> GetCatalogSnapshotAsync(CancellationToken cancellationToken = default)
            => innerBridge.GetCatalogSnapshotAsync(cancellationToken);

        public Task<FoundryAccelerationSnapshot> RegisterExecutionProvidersAsync(
            IReadOnlyList<string>? names = null,
            Action<string, double>? progress = null,
            CancellationToken cancellationToken = default)
            => innerBridge.RegisterExecutionProvidersAsync(names, progress, cancellationToken);

        public Task DownloadModelAsync(
            string alias,
            Action<double>? progress = null,
            CancellationToken cancellationToken = default)
            => innerBridge.DownloadModelAsync(alias, progress, cancellationToken);

        public Task UnloadModelAsync(
            string alias,
            CancellationToken cancellationToken = default)
            => innerBridge.UnloadModelAsync(alias, cancellationToken);

        public Task RemoveModelAsync(
            string alias,
            CancellationToken cancellationToken = default)
            => innerBridge.RemoveModelAsync(alias, cancellationToken);

        public Task<LlmModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
            => innerBridge.VerifyModelAvailabilityAsync(cancellationToken);

        public Task<LlmModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
            => innerBridge.GetModelInfoAsync(model, cancellationToken);

        public Task<LlmResponse> GenerateAsync(
            LlmRequest request,
            Action<LlmProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
            => innerBridge.GenerateAsync(request, progress, cancellationToken);

        public void Dispose()
        {
            GC.KeepAlive(loadContext);

            if (innerBridge is IDisposable disposableBridge)
            {
                disposableBridge.Dispose();
            }
        }
    }

    private sealed class PluginLoadContext(string pluginAssemblyPath) : AssemblyLoadContext($"FoundryWinMl:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}", isCollectible: false)
    {
        private readonly AssemblyDependencyResolver resolver = new(pluginAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var sharedAssembly = TryGetSharedAssembly(assemblyName.Name);
            if (sharedAssembly is not null)
            {
                return sharedAssembly;
            }

            var assemblyPath = resolver.ResolveAssemblyToPath(assemblyName);
            return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var unmanagedDllPath = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return unmanagedDllPath is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(unmanagedDllPath);
        }

        private static Assembly? TryGetSharedAssembly(string? assemblyName)
        {
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                return null;
            }

            if (!assemblyName.StartsWith("LiCvWriter.", StringComparison.Ordinal)
                && !assemblyName.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal))
            {
                return null;
            }

            return AssemblyLoadContext.Default.Assemblies
                .FirstOrDefault(loadedAssembly => string.Equals(
                    loadedAssembly.GetName().Name,
                    assemblyName,
                    StringComparison.Ordinal));
        }
    }
}