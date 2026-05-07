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

        if (video is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LocalVideoCopyPath)!);
            await video.SaveAsAsync(LocalVideoCopyPath);
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
            "# Playwright Demo Assets",
            string.Empty,
            "These demo assets are generated from local Playwright sessions against the LI-CV-Writer web app. The canonical tracked WebM is a full-app E2E walkthrough, while the screenshots below capture key Job Workbench batch states.",
            string.Empty,
            "Company names in screenshots and video are blurred by the Playwright masking helper before media is captured. Review the video before sharing outside the local workspace so privacy masking is confirmed across the full recording.",
            string.Empty,
            "Watch the recording on [the browser-playable Playwright demo page](playwright-demo.html).",
            string.Empty,
            "## Screenshot Walkthrough",
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
        markdown.Add("The full-app recording is tracked in the repository as [the LI-CV-Writer WebM walkthrough](assets/playwright-job-workbench-demo/job-workbench-demo.webm), so it is available from the online repo as well as the local workspace.");
        markdown.Add(string.Empty);
        markdown.Add("A local copy of the WebM and the diagnostic Playwright trace are also written under `artifacts/playwright/` when the demos are regenerated.");
        markdown.Add(string.Empty);
        markdown.Add("## Video Transcript");
        markdown.Add(string.Empty);
        markdown.Add("1. The recording opens on Start / Setup with LLM readiness, profile status, differentiators, and job-set count visible.");
        markdown.Add("2. It reviews the imported profile tabs, including experience, education, skills, certifications, projects, recommendations, and notes.");
        markdown.Add("3. It shows the redirect pages that now fold import, research, and generation back into the main setup and workbench flows.");
        markdown.Add("4. It walks through the Job Workbench overview, discovery, batch action settings, and job-set rows.");
        markdown.Add("5. It opens a job-set detail page to show research, fit review, technology gap, ranked evidence, draft generation, generated markdown drafts, and exported files.");
        markdown.Add("6. It returns to the overview, selects three job sets, clicks Start selected, and holds on live Status Monitor, Reasoning Monitor, and Activity feed updates.");
        markdown.Add(string.Empty);
        markdown.Add("## Validation");
        markdown.Add(string.Empty);
        markdown.Add("The artifact validators check that all expected screenshots exist, PNG dimensions are plausible, markdown links resolve, the tracked WebM has an EBML header, the WebM is larger than the configured threshold, and the trace archive contains trace and resource entries.");

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
