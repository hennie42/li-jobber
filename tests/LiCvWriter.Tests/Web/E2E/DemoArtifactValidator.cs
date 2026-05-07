using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace LiCvWriter.Tests.Web.E2E;

internal static partial class DemoArtifactValidator
{
    private const int ExpectedScreenshotCount = 4;
    private const long MinimumVideoBytes = 5_000_000;
    private const string ExpectedRepositoryVideoLink = "assets/playwright-job-workbench-demo/job-workbench-demo.webm";
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] WebMHeader = [0x1A, 0x45, 0xDF, 0xA3];

    public static void Validate(
        string repositoryRoot,
        string screenshotDirectory,
        string markdownPath,
        string videoPath,
        string tracePath,
        IReadOnlyList<DemoScreenshot> screenshots)
    {
        ValidateScreenshots(screenshotDirectory, screenshots);
        ValidateMarkdown(repositoryRoot, markdownPath, screenshots);
        ValidateWebM(videoPath);
        ValidateTrace(tracePath);
    }

    private static void ValidateScreenshots(string screenshotDirectory, IReadOnlyList<DemoScreenshot> screenshots)
    {
        if (screenshots.Count != ExpectedScreenshotCount)
        {
            throw new InvalidOperationException($"Expected {ExpectedScreenshotCount} demo screenshots, but captured {screenshots.Count}.");
        }

        foreach (var screenshot in screenshots)
        {
            var path = Path.Combine(screenshotDirectory, screenshot.FileName);
            var bytes = ReadRequiredBytes(path, 24, "demo screenshot");

            if (!bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            {
                throw new InvalidOperationException($"Demo screenshot is not a PNG file: {path}");
            }

            var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            if (width < 1_000 || height < 700)
            {
                throw new InvalidOperationException($"Demo screenshot has unexpected dimensions {width}x{height}: {path}");
            }
        }
    }

    private static void ValidateMarkdown(string repositoryRoot, string markdownPath, IReadOnlyList<DemoScreenshot> screenshots)
    {
        var markdown = File.Exists(markdownPath)
            ? File.ReadAllText(markdownPath)
            : throw new FileNotFoundException("Demo markdown was not generated.", markdownPath);

        foreach (var screenshot in screenshots)
        {
            var expectedLink = $"assets/playwright-job-workbench-demo/{screenshot.FileName}";
            if (!markdown.Contains(expectedLink, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Demo markdown does not reference expected screenshot link: {expectedLink}");
            }
        }

        if (!markdown.Contains(ExpectedRepositoryVideoLink, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Demo markdown does not reference the tracked WebM link: {ExpectedRepositoryVideoLink}");
        }

        var markdownDirectory = Path.GetDirectoryName(markdownPath)
            ?? throw new InvalidOperationException($"Could not resolve markdown directory for {markdownPath}.");

        foreach (Match match in MarkdownLinkPattern().Matches(markdown))
        {
            var link = match.Groups["path"].Value;
            if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var linkWithoutAnchor = link.Split('#', 2)[0];
            var resolvedPath = Path.GetFullPath(Path.Combine(markdownDirectory, linkWithoutAnchor.Replace('/', Path.DirectorySeparatorChar)));
            if (!resolvedPath.StartsWith(repositoryRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(resolvedPath))
            {
                throw new InvalidOperationException($"Demo markdown link does not resolve to a generated local file: {link}");
            }
        }
    }

    private static void ValidateWebM(string videoPath)
    {
        var fileInfo = new FileInfo(videoPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Demo WebM video was not generated.", videoPath);
        }

        if (fileInfo.Length <= MinimumVideoBytes)
        {
            throw new InvalidOperationException($"Demo WebM video is {fileInfo.Length:N0} bytes; expected more than {MinimumVideoBytes:N0} bytes.");
        }

        var bytes = ReadRequiredBytes(videoPath, WebMHeader.Length, "demo WebM video");
        if (!bytes.AsSpan(0, WebMHeader.Length).SequenceEqual(WebMHeader))
        {
            throw new InvalidOperationException($"Demo video does not start with the expected WebM EBML header: {videoPath}");
        }
    }

    private static void ValidateTrace(string tracePath)
    {
        var fileInfo = new FileInfo(tracePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Demo Playwright trace was not generated.", tracePath);
        }

        using var archive = ZipFile.OpenRead(tracePath);
        if (archive.Entries.Count < 3)
        {
            throw new InvalidOperationException($"Demo Playwright trace has too few entries: {tracePath}");
        }

        var traceEntries = archive.Entries.Where(entry => entry.FullName.EndsWith(".trace", StringComparison.OrdinalIgnoreCase)).ToList();
        var hasResources = archive.Entries.Any(entry => entry.FullName.StartsWith("resources/", StringComparison.OrdinalIgnoreCase));
        var hasNetwork = archive.Entries.Any(entry => entry.FullName.EndsWith(".network", StringComparison.OrdinalIgnoreCase));
        var hasSnapshots = traceEntries.Any(entry => EntryContains(entry, "frame-snapshot") || EntryContains(entry, "snapshot"));

        if (traceEntries.Count == 0 || !hasResources || !hasNetwork || !hasSnapshots)
        {
            throw new InvalidOperationException("Demo Playwright trace is missing expected trace, network, resource, or snapshot evidence.");
        }
    }

    private static byte[] ReadRequiredBytes(string path, int count, string artifactLabel)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required {artifactLabel} was not generated.", path);
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < count)
        {
            throw new InvalidOperationException($"Required {artifactLabel} is too small to validate: {path}");
        }

        return bytes;
    }

    private static bool EntryContains(ZipArchiveEntry entry, string value)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var buffer = new char[4096];
        int read;
        while ((read = reader.ReadBlock(buffer, 0, buffer.Length)) > 0)
        {
            if (buffer.AsSpan(0, read).Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"!?\[[^\]]+\]\((?<path>[^)]+)\)", RegexOptions.Compiled)]
    private static partial Regex MarkdownLinkPattern();
}