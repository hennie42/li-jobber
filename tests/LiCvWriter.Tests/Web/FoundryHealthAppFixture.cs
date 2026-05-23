using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LiCvWriter.Tests.Web;

public sealed class FoundryHealthAppFixture : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly List<string> outputLines = [];
    private Process? appProcess;

    public string RepositoryRoot { get; private set; } = string.Empty;

    public string BaseUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsWindows() || !LiveWindowsFoundryFactAttribute.IsEnabled)
        {
            return;
        }

        RepositoryRoot = FindRepositoryRoot();

        var port = GetFreeTcpPort();
        BaseUrl = $"http://127.0.0.1:{port}";
        appProcess = StartAppProcess(port);
        await WaitForAppAsync(TimeSpan.FromSeconds(90));
    }

    public async Task DisposeAsync()
    {
        if (appProcess is not null && !appProcess.HasExited)
        {
            appProcess.Kill(entireProcessTree: true);
            await appProcess.WaitForExitAsync();
        }
    }

    public async Task<T> GetJsonAsync<T>(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            throw new InvalidOperationException("The LI-CV-Writer web app was not initialized. Set LICVWRITER_RUN_WINDOWS_FOUNDRY_SMOKE=1 before running this test.");
        }

        using var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        using var response = await httpClient.GetAsync($"{BaseUrl}{relativePath}");
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Request to '{relativePath}' failed with {(int)response.StatusCode}: {body}\n\nApp output:\n{GetRecentOutput()}");
        }

        return JsonSerializer.Deserialize<T>(body, SerializerOptions)
            ?? throw new InvalidOperationException($"Request to '{relativePath}' returned an empty JSON payload.");
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
        processStartInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";

        var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("Failed to start the LI-CV-Writer web app for the Windows Foundry smoke test.");

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
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}