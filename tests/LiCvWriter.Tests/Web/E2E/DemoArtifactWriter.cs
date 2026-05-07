using Microsoft.Playwright;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class DemoArtifactWriter
{
    private readonly string repositoryRoot;
    private readonly bool enabled;
    private readonly List<DemoScreenshot> screenshots = [];
    private bool traceStarted;

    public DemoArtifactWriter(string repositoryRoot)
    {
        this.repositoryRoot = repositoryRoot;
        enabled = string.Equals(Environment.GetEnvironmentVariable("LICVWRITER_PLAYWRIGHT_WRITE_DEMO"), "1", StringComparison.OrdinalIgnoreCase);
        ScreenshotDirectory = Path.Combine(repositoryRoot, "docs", "assets", "playwright-job-workbench-demo");
        VideoPath = Path.Combine(ScreenshotDirectory, "job-workbench-demo.webm");
        LocalVideoCopyPath = Path.Combine(repositoryRoot, "artifacts", "playwright", "job-workbench-demo.webm");
        TracePath = Path.Combine(repositoryRoot, "artifacts", "playwright", "job-workbench-demo-trace.zip");
        MarkdownPath = Path.Combine(repositoryRoot, "docs", "playwright-job-workbench-demo.md");
    }

    public string ScreenshotDirectory { get; }

    public string VideoPath { get; }

    public string LocalVideoCopyPath { get; }

    public string TracePath { get; }

    public string MarkdownPath { get; }

    public bool Enabled => enabled;

    public async Task StartTraceAsync(IBrowserContext context)
    {
        if (!enabled)
        {
            return;
        }

        Directory.CreateDirectory(ScreenshotDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(TracePath)!);
        DeleteIfExists(VideoPath);
        DeleteIfExists(LocalVideoCopyPath);
        DeleteIfExists(TracePath);
        await context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true
        });
        traceStarted = true;
    }

    public async Task CaptureAsync(IPage page, string fileName, string altText)
    {
        if (!enabled)
        {
            return;
        }

        Directory.CreateDirectory(ScreenshotDirectory);
        var path = Path.Combine(ScreenshotDirectory, fileName);
        await CompanyNameMasker.ApplyAsync(page);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = false });
        screenshots.Add(new DemoScreenshot(fileName, altText));
    }

    public async Task CompleteAsync(IPage page, IBrowserContext context, bool validateArtifacts = true)
    {
        var video = page.Video;

        if (enabled && traceStarted)
        {
            await context.Tracing.StopAsync(new TracingStopOptions { Path = TracePath });
        }

        await context.CloseAsync();

        if (!enabled)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(VideoPath)!);
        if (video is not null)
        {
            await video.SaveAsAsync(VideoPath);
            Directory.CreateDirectory(Path.GetDirectoryName(LocalVideoCopyPath)!);
            File.Copy(VideoPath, LocalVideoCopyPath, overwrite: true);
        }

        if (validateArtifacts)
        {
            WriteMarkdown();
            DemoArtifactValidator.Validate(repositoryRoot, ScreenshotDirectory, MarkdownPath, VideoPath, TracePath, screenshots);
        }
    }

    private void WriteMarkdown()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkdownPath)!);
        var markdown = new List<string>
        {
            "# Playwright Job Workbench Demo",
            string.Empty,
            "This guided walkthrough was generated from a local Playwright session against the LI-CV-Writer web app. The session seeds three ready job sets, selects them, starts the batch run, and records live LLM activity in the workbench monitors.",
            string.Empty,
            "Company names in screenshots and video are blurred by the Playwright masking helper before media is captured. Review the video before sharing outside the local workspace so privacy masking is confirmed across the full recording.",
            string.Empty,
            "## Walkthrough",
            string.Empty,
            "1. Open the Job Workbench with a loaded profile, a live Ollama model, and three ready job sets.",
            "2. Review each job-set row while the company-name masking remains visible.",
            "3. Select the three job sets in the batch list.",
            "4. Click Start selected.",
            "5. Watch the batch label, job-set status chips, Status Monitor, Reasoning Monitor, and Activity feed update while the LLM operation runs.",
            string.Empty
        };

        foreach (var screenshot in screenshots)
        {
            markdown.Add($"![{screenshot.AltText}](assets/playwright-job-workbench-demo/{screenshot.FileName})");
            markdown.Add(string.Empty);
        }

        markdown.Add("## Video");
        markdown.Add(string.Empty);
        markdown.Add("The guided recording is tracked in the repository as [the Job Workbench WebM walkthrough](assets/playwright-job-workbench-demo/job-workbench-demo.webm), so it is available from the online repo as well as the local workspace.");
        markdown.Add(string.Empty);
        markdown.Add("A local copy of the WebM and the diagnostic Playwright trace are also written under `artifacts/playwright/` when the demo is regenerated.");
        markdown.Add(string.Empty);
        markdown.Add("## Video Transcript");
        markdown.Add(string.Empty);
        markdown.Add("1. The recording opens on the seeded Job Workbench and reviews the three ready job sets with company names blurred.");
        markdown.Add("2. Each job set is selected one at a time so the batch queue state is visible.");
        markdown.Add("3. The Start selected command is highlighted before the batch begins.");
        markdown.Add("4. The recording holds on the running batch label, Status Monitor, Reasoning Monitor, and Activity feed while live LLM output appears.");
        markdown.Add("5. The walkthrough returns to the workbench rows to show the updated labels and status chips.");
        markdown.Add(string.Empty);
        markdown.Add("## Validation");
        markdown.Add(string.Empty);
        markdown.Add("The artifact validator checks that all expected screenshots exist, PNG dimensions are plausible, markdown links resolve, the tracked WebM has an EBML header, the WebM is larger than 5 MB, and the trace archive contains trace and resource entries.");

        File.WriteAllText(MarkdownPath, string.Join(Environment.NewLine, markdown) + Environment.NewLine);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

internal sealed record DemoScreenshot(string FileName, string AltText);
