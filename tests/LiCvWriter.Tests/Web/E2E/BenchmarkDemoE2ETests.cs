using Microsoft.Playwright;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class BenchmarkDemoE2ETests(PlaywrightAppFixture fixture) : IClassFixture<PlaywrightAppFixture>
{
    private const string LocalVideoFileName = "benchmark-demo.webm";
    private const string RepositoryVideoFileName = "benchmark-demo.webm";
    private const long MinimumVideoBytes = 8_000_000;
    private static readonly byte[] WebMHeader = [0x1A, 0x45, 0xDF, 0xA3];

    [BenchmarkDemoFact]
    public async Task RecordCombinedOllamaAndFoundryBenchmarkDemo_WritesArtifactVideo()
    {
        var seed = await fixture.SeedDemoAsync();
        var localVideoPath = Path.Combine(fixture.ArtifactRoot, LocalVideoFileName);
        var repositoryVideoPath = Path.Combine(
            fixture.RepositoryRoot,
            "docs",
            "assets",
            "playwright-benchmark-demo",
            RepositoryVideoFileName);
        DeleteIfExists(localVideoPath);

        var context = await fixture.CreateContextAsync(recordVideo: true);
        IPage? page = null;
        var flowCompleted = false;

        try
        {
            page = await context.NewPageAsync();
            var setupPage = new LlmSetupPage(page, fixture.BaseUrl);
            var walkthrough = new BenchmarkDemoWalkthrough(page, setupPage, seed.Model);

            await RunWithTimeoutAsync("record combined benchmark demo", walkthrough.RecordAsync, TimeSpan.FromMinutes(16));
            flowCompleted = true;
        }
        finally
        {
            if (page is not null)
            {
                var video = page.Video;
                await context.CloseAsync();

                if (video is not null)
                {
                    await video.SaveAsAsync(localVideoPath);
                }
            }
            else
            {
                await context.CloseAsync();
            }
        }

        if (flowCompleted)
        {
            ValidateWebM(localVideoPath);
            CopyValidatedVideo(localVideoPath, repositoryVideoPath);
            ValidateWebM(repositoryVideoPath);
        }
    }

    private static void CopyValidatedVideo(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static async Task RunWithTimeoutAsync(string stage, Func<Task> action, TimeSpan timeout)
    {
        try
        {
            await action().WaitAsync(timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Playwright benchmark demo stage '{stage}' exceeded {timeout}.", exception);
        }
    }

    private static void ValidateWebM(string videoPath)
    {
        var fileInfo = new FileInfo(videoPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Benchmark demo WebM video was not generated.", videoPath);
        }

        if (fileInfo.Length <= MinimumVideoBytes)
        {
            throw new InvalidOperationException($"Benchmark demo WebM video is {fileInfo.Length:N0} bytes; expected more than {MinimumVideoBytes:N0} bytes.");
        }

        var headerBytes = File.ReadAllBytes(videoPath).AsSpan(0, WebMHeader.Length);
        if (!headerBytes.SequenceEqual(WebMHeader))
        {
            throw new InvalidOperationException($"Benchmark demo video does not start with the expected WebM EBML header: {videoPath}");
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}