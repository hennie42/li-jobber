namespace LiCvWriter.Web.Services;

/// <summary>
/// Maps exported-file download endpoints and keeps download path allowlist checks beside the route.
/// </summary>
internal static class ExportedFileEndpointExtensions
{
    public static IEndpointRouteBuilder MapLiCvWriterExportedFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/files/exported", (string? path, WorkspaceSession workspace) =>
        {
            var requestedPath = NormalizeExportDownloadPath(path);
            if (requestedPath is null || !IsKnownExportedFile(workspace, requestedPath))
            {
                return Results.NotFound();
            }

            var fullPath = Path.GetFullPath(requestedPath);
            if (!File.Exists(fullPath))
            {
                return Results.NotFound();
            }

            return Results.File(fullPath, "application/octet-stream", Path.GetFileName(fullPath));
        });

        return app;
    }

    private static bool IsKnownExportedFile(WorkspaceSession workspace, string requestedPath)
    {
        string requestedFullPath;
        try
        {
            requestedFullPath = Path.GetFullPath(requestedPath.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return workspace.JobSets
            .SelectMany(static jobSet => jobSet.Exports)
            .Any(export => IsSamePath(export.FilePath, requestedFullPath));
    }

    private static string? NormalizeExportDownloadPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Trim();
        if (OperatingSystem.IsWindows() && normalized.EndsWith("?", StringComparison.Ordinal))
        {
            normalized = normalized.TrimEnd('?');
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool IsSamePath(string? candidatePath, string requestedFullPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(candidatePath.Trim()), requestedFullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}