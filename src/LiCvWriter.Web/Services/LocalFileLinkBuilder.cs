namespace LiCvWriter.Web.Services;

public static class LocalFileLinkBuilder
{
    public static string BuildFileUri(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "#";
        }

        var normalized = filePath.Trim();
        if (TryBuildWindowsDriveUri(normalized, out var windowsDriveUri))
        {
            return windowsDriveUri;
        }

        if (TryBuildUncUri(normalized, out var uncUri))
        {
            return uncUri;
        }

        try
        {
            return new Uri(Path.GetFullPath(normalized)).AbsoluteUri;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "#";
        }
    }

    public static string BuildFolderUri(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "#";
        }

        string? folder;
        try
        {
            folder = Path.GetDirectoryName(filePath.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "#";
        }

        return string.IsNullOrWhiteSpace(folder) ? "#" : BuildFileUri(folder);
    }

    private static bool TryBuildWindowsDriveUri(string path, out string uri)
    {
        uri = string.Empty;
        if (path.Length < 3
            || !char.IsLetter(path[0])
            || path[1] != ':'
            || !IsSlash(path[2]))
        {
            return false;
        }

        uri = new Uri("file:///" + path.Replace('\\', '/')).AbsoluteUri;
        return true;
    }

    private static bool TryBuildUncUri(string path, out string uri)
    {
        uri = string.Empty;
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        uri = new Uri("file:" + path.Replace('\\', '/')).AbsoluteUri;
        return true;
    }

    private static bool IsSlash(char character)
        => character is '\\' or '/';
}