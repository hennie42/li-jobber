using LiCvWriter.Application.Options;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace LiCvWriter.Infrastructure.Llm;

/// <summary>
/// Lazily initializes the Foundry Local singleton manager and exposes it to infrastructure services.
/// </summary>
public sealed class FoundryLocalManagerAccessor(FoundryOptions options, ILoggerFactory loggerFactory) : IDisposable
{
    private static readonly Regex InvalidAppNameCharacters = new("[^\\p{L}\\p{Nd} _-]+", RegexOptions.Compiled);
    private readonly SemaphoreSlim initializationGate = new(1, 1);

    public async Task<FoundryLocalManager> GetManagerAsync(CancellationToken cancellationToken = default)
    {
        if (FoundryLocalManager.IsInitialized)
        {
            return FoundryLocalManager.Instance;
        }

        await initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (!FoundryLocalManager.IsInitialized)
            {
                try
                {
                    await FoundryLocalManager.CreateAsync(BuildConfiguration(options), loggerFactory.CreateLogger("FoundryLocal"));
                }
                catch (Exception exception)
                {
                    var normalizedException = NormalizeInitializationException(exception);
                    if (ReferenceEquals(normalizedException, exception))
                    {
                        throw;
                    }

                    throw normalizedException;
                }
            }

            return FoundryLocalManager.Instance;
        }
        finally
        {
            initializationGate.Release();
        }
    }

    public void Dispose()
    {
        initializationGate.Dispose();

        if (FoundryLocalManager.IsInitialized)
        {
            FoundryLocalManager.Instance.Dispose();
        }
    }

    private static Configuration BuildConfiguration(FoundryOptions options)
        => new()
        {
            AppName = NormalizeAppName(options.AppName),
            AppDataDir = NormalizeOptionalPath(options.AppDataDir),
            ModelCacheDir = NormalizeOptionalPath(options.ModelCacheDir),
            LogsDir = NormalizeOptionalPath(options.LogsDir)
        };

    private static Exception NormalizeInitializationException(Exception exception)
    {
        var nativeCoreException = FindException<DllNotFoundException>(exception);
        if (nativeCoreException is null
            || !nativeCoreException.Message.Contains("Microsoft.AI.Foundry.Local.Core", StringComparison.Ordinal))
        {
            return exception;
        }

        var message = OperatingSystem.IsWindows()
            ? "Microsoft Foundry Local could not start because its native runtime core is missing on this machine. The managed SDK loaded, but Microsoft.AI.Foundry.Local.Core was not found at runtime. This Windows environment still needs the Windows-specific Foundry runtime/package setup before catalog and model operations can run."
            : "Microsoft Foundry Local could not start because its native runtime core is missing on this machine. The managed SDK loaded, but Microsoft.AI.Foundry.Local.Core was not found at runtime.";

        return new InvalidOperationException(message, exception);
    }

    private static string? NormalizeOptionalPath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    private static string NormalizeAppName(string? appName)
    {
        var candidate = string.IsNullOrWhiteSpace(appName) ? "LI-CV-Writer" : appName.Trim();
        candidate = InvalidAppNameCharacters.Replace(candidate, "-");

        return string.IsNullOrWhiteSpace(candidate) ? "LI-CV-Writer" : candidate;
    }

    private static TException? FindException<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException matchingException)
            {
                return matchingException;
            }
        }

        return null;
    }
}