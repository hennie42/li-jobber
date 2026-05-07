using Microsoft.Playwright;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class FullAppDemoE2ETests(PlaywrightAppFixture fixture) : IClassFixture<PlaywrightAppFixture>
{
    private const string VideoFileName = "full-app-e2e-demo.webm";
    private const string RepositoryVideoFileName = "job-workbench-demo.webm";
    private const long MinimumVideoBytes = 8_000_000;
    private static readonly byte[] WebMHeader = [0x1A, 0x45, 0xDF, 0xA3];

    [FullAppDemoFact]
    public async Task RecordFullAppDemo_WithFullSeed_WritesArtifactVideo()
    {
        var seed = await fixture.SeedDemoAsync("full");
        var videoPath = Path.Combine(fixture.ArtifactRoot, VideoFileName);
        var repositoryVideoPath = Path.Combine(
            fixture.RepositoryRoot,
            "docs",
            "assets",
            "playwright-job-workbench-demo",
            RepositoryVideoFileName);
        DeleteIfExists(videoPath);

        var context = await fixture.CreateContextAsync(recordVideo: true);
        await CompanyNameMasker.InstallAsync(context, seed.CompanyNames);

        IPage? page = null;
        var flowCompleted = false;

        try
        {
            page = await context.NewPageAsync();
            var workbench = new JobWorkbenchPage(page, fixture.BaseUrl);
            var walkthrough = new FullAppDemoWalkthrough(page, fixture.BaseUrl, workbench, seed.CompanyNames, seed.JobSetIds);

            await RunWithTimeoutAsync("record full-app demo", walkthrough.RecordAsync, TimeSpan.FromMinutes(8));
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
                    await video.SaveAsAsync(videoPath);
                }
            }
            else
            {
                await context.CloseAsync();
            }
        }

        if (flowCompleted)
        {
            ValidateWebM(videoPath);
            CopyValidatedVideo(videoPath, repositoryVideoPath);
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
            throw new TimeoutException($"Playwright full-app demo stage '{stage}' exceeded {timeout}.", exception);
        }
    }

    private static void ValidateWebM(string videoPath)
    {
        var fileInfo = new FileInfo(videoPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Full-app demo WebM video was not generated.", videoPath);
        }

        if (fileInfo.Length <= MinimumVideoBytes)
        {
            throw new InvalidOperationException($"Full-app demo WebM video is {fileInfo.Length:N0} bytes; expected more than {MinimumVideoBytes:N0} bytes.");
        }

        var headerBytes = File.ReadAllBytes(videoPath).AsSpan(0, WebMHeader.Length);
        if (!headerBytes.SequenceEqual(WebMHeader))
        {
            throw new InvalidOperationException($"Full-app demo video does not start with the expected WebM EBML header: {videoPath}");
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