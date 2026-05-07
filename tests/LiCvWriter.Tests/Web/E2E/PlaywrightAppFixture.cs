using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Playwright;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class PlaywrightAppFixture : IAsyncLifetime
{
    private readonly List<string> outputLines = [];
    private IPlaywright? playwright;
    private IBrowser? browser;
    private Process? appProcess;

    public string RepositoryRoot { get; private set; } = string.Empty;

    public string BaseUrl { get; private set; } = string.Empty;

    public string ArtifactRoot => Path.Combine(RepositoryRoot, "artifacts", "playwright");

    public async Task InitializeAsync()
    {
        if (!LivePlaywrightFactAttribute.IsEnabled)
        {
            return;
        }

        RepositoryRoot = FindRepositoryRoot();
        Directory.CreateDirectory(ArtifactRoot);

        var port = GetFreeTcpPort();
        BaseUrl = $"http://127.0.0.1:{port}";
        appProcess = StartAppProcess(port);
        await WaitForAppAsync(TimeSpan.FromSeconds(90));

        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = !string.Equals(Environment.GetEnvironmentVariable("LICVWRITER_PLAYWRIGHT_HEADLESS"), "0", StringComparison.OrdinalIgnoreCase)
        });
    }

    public async Task DisposeAsync()
    {
        if (browser is not null)
        {
            await browser.DisposeAsync();
        }

        playwright?.Dispose();

        if (appProcess is not null && !appProcess.HasExited)
        {
            appProcess.Kill(entireProcessTree: true);
            await appProcess.WaitForExitAsync();
        }
    }

    public async Task<IBrowserContext> CreateContextAsync(bool recordVideo)
    {
        if (browser is null)
        {
            throw new InvalidOperationException("Playwright was not initialized. Set LICVWRITER_RUN_PLAYWRIGHT_E2E=1 before running this test.");
        }

        var options = new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 },
            IgnoreHTTPSErrors = true
        };

        if (recordVideo)
        {
            var videoDirectory = Path.Combine(ArtifactRoot, "videos", DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(videoDirectory);
            options.RecordVideoDir = videoDirectory;
            options.RecordVideoSize = new RecordVideoSize { Width = 1440, Height = 1000 };
        }

        var context = await browser.NewContextAsync(options);
        context.SetDefaultTimeout(60_000);
        context.SetDefaultNavigationTimeout(60_000);

        return context;
    }

    public async Task<PlaywrightDemoSeedResponse> SeedDemoAsync()
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var response = await httpClient.PostAsync($"{BaseUrl}/api/playwright/demo-seed", null);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Playwright demo seed failed with {(int)response.StatusCode}: {body}\n\nApp output:\n{GetRecentOutput()}");
        }

        return JsonSerializer.Deserialize<PlaywrightDemoSeedResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Playwright demo seed returned an empty response.");
    }

    private Process StartAppProcess(int port)
    {
        var projectPath = Path.Combine(RepositoryRoot, "src", "LiCvWriter.Web", "LiCvWriter.Web.csproj");
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        processStartInfo.ArgumentList.Add("run");
        processStartInfo.ArgumentList.Add("--no-build");
        processStartInfo.ArgumentList.Add("--no-launch-profile");
        processStartInfo.ArgumentList.Add("--project");
        processStartInfo.ArgumentList.Add(projectPath);
        processStartInfo.ArgumentList.Add("--urls");
        processStartInfo.ArgumentList.Add($"http://127.0.0.1:{port}");

        processStartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        processStartInfo.Environment["Playwright__EnableDemoSeed"] = "true";
        processStartInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";

        var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("Failed to start the LI-CV-Writer web app for Playwright.");

        process.OutputDataReceived += (_, args) => AppendOutput(args.Data);
        process.ErrorDataReceived += (_, args) => AppendOutput(args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private async Task WaitForAppAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        using var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (appProcess is { HasExited: true })
            {
                throw new InvalidOperationException($"The LI-CV-Writer web app exited before it became ready.\n\nApp output:\n{GetRecentOutput()}");
            }

            try
            {
                using var response = await httpClient.GetAsync(BaseUrl);
                if (response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.BadRequest)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"The LI-CV-Writer web app did not respond within {timeout}.\n\nApp output:\n{GetRecentOutput()}");
    }

    private string GetRecentOutput()
        => string.Join(Environment.NewLine, outputLines.TakeLast(80));

    private void AppendOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (outputLines)
        {
            outputLines.Add(line);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LiCvWriter.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate LiCvWriter.sln from the test output directory.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
