using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.RegularExpressions;

namespace FoundryLocal.WindowsProbe;

internal static class Program
{
    private static readonly Regex InvalidAppNameCharacters = new("[^\\p{L}\\p{Nd} _-]+", RegexOptions.Compiled);
    private const string DefaultAppName = "LI-CV-Writer WindowsProbe";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseOptions(args);
            var configuration = BuildConfiguration(options.AppName);

            await FoundryLocalManager.CreateAsync(configuration, NullLogger.Instance);
            WriteExecutionProviderSnapshot(options.AppName);

            if (!options.RunLifecycleCheck)
            {
                return 0;
            }

            return await RunLifecycleCheckAsync(configuration);
        }
        catch (Exception exception)
        {
            WriteException(exception);
            return 1;
        }
        finally
        {
            if (FoundryLocalManager.IsInitialized)
            {
                FoundryLocalManager.Instance.Dispose();
            }
        }
    }

    /// <summary>
    /// Parses the probe arguments into a small immutable options record.
    /// </summary>
    private static ProbeOptions ParseOptions(string[] args)
    {
        string? appName = null;
        var runLifecycleCheck = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--lifecycle":
                    runLifecycleCheck = true;
                    break;

                case "--app-name" when index + 1 < args.Length:
                    appName = args[++index];
                    break;

                case var _ when !string.IsNullOrWhiteSpace(argument) && !argument.StartsWith("--", StringComparison.Ordinal):
                    appName ??= argument;
                    break;
            }
        }

        return new ProbeOptions(ResolveAppName(appName), runLifecycleCheck);
    }

    /// <summary>
    /// Builds the Foundry Local configuration used by the probe.
    /// </summary>
    private static Configuration BuildConfiguration(string appName)
        => new()
        {
            AppName = appName,
            LogLevel = Microsoft.AI.Foundry.Local.LogLevel.Warning
        };

    /// <summary>
    /// Prints the current execution-provider snapshot for the initialized manager.
    /// </summary>
    private static void WriteExecutionProviderSnapshot(string appName)
    {
        Console.WriteLine($"Foundry Local initialized for {appName}.");
        Console.WriteLine($"IsInitialized after create: {FoundryLocalManager.IsInitialized}");

        foreach (var executionProvider in FoundryLocalManager.Instance
                     .DiscoverEps()
                     .OrderBy(static executionProvider => executionProvider.Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"{executionProvider.Name}: registered={executionProvider.IsRegistered}");
        }
    }

    /// <summary>
    /// Verifies whether disposing the manager leaves the SDK in a state that can be safely reinitialized.
    /// </summary>
    private static async Task<int> RunLifecycleCheckAsync(Configuration configuration)
    {
        var originalManager = FoundryLocalManager.Instance;
        Console.WriteLine("Lifecycle check: disposing the current manager instance.");
        originalManager.Dispose();

        var stillInitializedAfterDispose = FoundryLocalManager.IsInitialized;
        Console.WriteLine($"IsInitialized after dispose: {stillInitializedAfterDispose}");
        if (stillInitializedAfterDispose)
        {
            Console.Error.WriteLine("Lifecycle check failed: FoundryLocalManager.IsInitialized stayed true after Dispose(). In-process reset is not safe on this SDK build.");
            return 2;
        }

        try
        {
            await FoundryLocalManager.CreateAsync(configuration, NullLogger.Instance);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Lifecycle check failed: reinitialization after Dispose() threw an exception.");
            WriteException(exception);
            return 3;
        }

        var recreatedManager = FoundryLocalManager.Instance;
        var reusedDisposedInstance = ReferenceEquals(originalManager, recreatedManager);
        Console.WriteLine($"IsInitialized after recreate: {FoundryLocalManager.IsInitialized}");
        Console.WriteLine($"Recreated manager reused original instance: {reusedDisposedInstance}");

        if (reusedDisposedInstance)
        {
            Console.Error.WriteLine("Lifecycle check failed: reinitialization returned the same manager instance after Dispose().");
            return 4;
        }

        Console.WriteLine("Lifecycle check passed: Dispose() cleared initialization state and CreateAsync() returned a fresh manager instance.");
        return 0;
    }

    /// <summary>
    /// Resolves the application name passed to the Foundry Local runtime.
    /// </summary>
    private static string ResolveAppName(string? appName)
    {
        var candidate = string.IsNullOrWhiteSpace(appName) ? DefaultAppName : appName.Trim();
        candidate = InvalidAppNameCharacters.Replace(candidate, "-");

        return string.IsNullOrWhiteSpace(candidate) ? DefaultAppName : candidate;
    }

    /// <summary>
    /// Writes the full exception chain so runtime package issues are easy to diagnose.
    /// </summary>
    private static void WriteException(Exception exception)
    {
        Console.Error.WriteLine($"{exception.GetType().FullName}: {exception.Message}");

        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            Console.Error.WriteLine($"Inner: {current.GetType().FullName}: {current.Message}");
        }
    }

    private sealed record ProbeOptions(string AppName, bool RunLifecycleCheck);
}