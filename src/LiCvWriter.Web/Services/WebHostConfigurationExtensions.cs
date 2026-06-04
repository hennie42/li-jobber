namespace LiCvWriter.Web.Services;

/// <summary>
/// Configures host-level diagnostics and local static web asset discovery for the web app.
/// </summary>
internal static class WebHostConfigurationExtensions
{
    public static void TryEnableLiCvWriterStaticWebAssets(this WebApplicationBuilder builder)
    {
        if (!string.IsNullOrWhiteSpace(builder.Configuration[Microsoft.AspNetCore.Hosting.WebHostDefaults.StaticWebAssetsKey]))
        {
            return;
        }

        var objDirectory = Path.Combine(builder.Environment.ContentRootPath, "obj");
        if (!Directory.Exists(objDirectory))
        {
            return;
        }

        var manifestPath = Directory.EnumerateFiles(objDirectory, "staticwebassets.development.json", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return;
        }

        builder.Configuration[Microsoft.AspNetCore.Hosting.WebHostDefaults.StaticWebAssetsKey] = manifestPath;
        Microsoft.AspNetCore.Hosting.StaticWebAssets.StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);
    }

    public static void RegisterLiCvWriterHostLifetimeDiagnostics(this WebApplication app, ILogger logger)
    {
        var lifetime = app.Lifetime;
        var processId = Environment.ProcessId;

        lifetime.ApplicationStarted.Register(() =>
            TryLogHostLifetime(() => logger.LogInformation("Application started. ProcessId={ProcessId} Environment={EnvironmentName} ContentRoot={ContentRoot}",
                processId,
                app.Environment.EnvironmentName,
                app.Environment.ContentRootPath)));

        lifetime.ApplicationStopping.Register(() =>
            TryLogHostLifetime(() => logger.LogWarning("Application stopping. ProcessId={ProcessId}", processId)));

        lifetime.ApplicationStopped.Register(() =>
            TryLogHostLifetime(() => logger.LogWarning("Application stopped. ProcessId={ProcessId}", processId)));

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            TryLogHostLifetime(() => logger.LogWarning("Process exit raised. ProcessId={ProcessId}", processId));

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                TryLogHostLifetime(() => logger.LogCritical(exception,
                    "Unhandled exception reached AppDomain. ProcessId={ProcessId} IsTerminating={IsTerminating}",
                    processId,
                    eventArgs.IsTerminating));
                return;
            }

            TryLogHostLifetime(() => logger.LogCritical(
                "Unhandled non-exception object reached AppDomain. ProcessId={ProcessId} IsTerminating={IsTerminating} PayloadType={PayloadType}",
                processId,
                eventArgs.IsTerminating,
                eventArgs.ExceptionObject?.GetType().FullName ?? "<null>"));
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            TryLogHostLifetime(() => logger.LogError(eventArgs.Exception,
                "Unobserved task exception captured. ProcessId={ProcessId}",
                processId));
            eventArgs.SetObserved();
        };
    }

    private static void TryLogHostLifetime(Action logAction)
    {
        try
        {
            logAction();
        }
        catch (Exception exception) when (IsDisposedLoggingException(exception))
        {
        }
    }

    private static bool IsDisposedLoggingException(Exception exception)
        => exception is ObjectDisposedException
            || exception is AggregateException aggregateException
            && aggregateException.Flatten().InnerExceptions.Any(IsDisposedLoggingException);
}