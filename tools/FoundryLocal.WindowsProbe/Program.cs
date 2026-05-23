using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.RegularExpressions;

namespace FoundryLocal.WindowsProbe;

internal static class Program
{
    private static readonly Regex InvalidAppNameCharacters = new("[^\\p{L}\\p{Nd} _-]+", RegexOptions.Compiled);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var appName = ResolveAppName(args);
            var configuration = new Configuration
            {
                AppName = appName,
                LogLevel = Microsoft.AI.Foundry.Local.LogLevel.Warning
            };

            await FoundryLocalManager.CreateAsync(configuration, NullLogger.Instance);

            Console.WriteLine($"Foundry Local initialized for {appName}.");
            foreach (var executionProvider in FoundryLocalManager.Instance
                         .DiscoverEps()
                         .OrderBy(static executionProvider => executionProvider.Name, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{executionProvider.Name}: registered={executionProvider.IsRegistered}");
            }

            return 0;
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
    /// Resolves the application name passed to the Foundry Local runtime.
    /// </summary>
    private static string ResolveAppName(string[] args)
    {
        var candidate = args.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim()
            ?? "LI-CV-Writer WindowsProbe";
        candidate = InvalidAppNameCharacters.Replace(candidate, "-");

        return string.IsNullOrWhiteSpace(candidate) ? "LI-CV-Writer WindowsProbe" : candidate;
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
}