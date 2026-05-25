using Microsoft.Playwright;
using Xunit.Sdk;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class LlmReasoningMonitorE2ETests(PlaywrightAppFixture fixture) : IClassFixture<PlaywrightAppFixture>
{
    private static readonly string[] PreferredReasoningAliases =
    [
        "phi-4-reasoning",
        "phi-4-reasoning-plus",
        "deepseek-r1-1.5b",
        "deepseek-r1-14b",
        "deepseek-r1-distill-qwen-14b",
        "deepseek-r1"
    ];

    [LivePlaywrightFact(1_200_000)]
    public async Task RunSingleFoundryBenchmark_WhenReasoningModelIsAvailable_ShowsReasoningAndTokenTelemetryInSidebar()
    {
        var context = await fixture.CreateContextAsync(recordVideo: false);
        IPage? page = null;

        try
        {
            page = await context.NewPageAsync();
            var setupPage = new LlmSetupPage(page, fixture.BaseUrl);

            await RunWithTimeoutAsync("open llm setup", setupPage.GotoAsync, TimeSpan.FromMinutes(2));
            await RunWithTimeoutAsync("load foundry catalog", setupPage.SelectFoundryProviderAsync, TimeSpan.FromMinutes(3));
            await RunWithTimeoutAsync("clear prior model selection", setupPage.ClearSelectedModelsAsync, TimeSpan.FromMinutes(1));

            string targetAlias = string.Empty;
            await RunWithTimeoutAsync("choose reasoning benchmark target", async () =>
            {
                targetAlias = SelectReasoningAlias(await setupPage.GetVisibleAliasesAsync());
            }, TimeSpan.FromMinutes(2));

            await RunWithTimeoutAsync("select reasoning benchmark target", () => setupPage.SelectFoundryModelAsync(targetAlias), TimeSpan.FromMinutes(2));
            await RunWithTimeoutAsync("verify single selected model", () => setupPage.WaitForSelectedModelCountAsync(1, TimeSpan.FromMinutes(1)), TimeSpan.FromMinutes(2));
            await RunWithTimeoutAsync("start reasoning benchmark", setupPage.StartBenchmarkAsync, TimeSpan.FromMinutes(1));
            await RunWithTimeoutAsync("show live queue", () => setupPage.WaitForLiveQueueTotalAsync(1, TimeSpan.FromMinutes(1)), TimeSpan.FromMinutes(2));
            await RunWithTimeoutAsync("show activity monitor telemetry", () => setupPage.WaitForActivityMonitorTelemetryAsync(targetAlias, TimeSpan.FromMinutes(8)), TimeSpan.FromMinutes(9));
            await RunWithTimeoutAsync("show reasoning monitor text", () => setupPage.WaitForReasoningMonitorToShowCapturedTextAsync(TimeSpan.FromMinutes(8)), TimeSpan.FromMinutes(9));
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Playwright reasoning-monitor regression failed.{Environment.NewLine}{exception.Message}{Environment.NewLine}{Environment.NewLine}App output:{Environment.NewLine}{fixture.GetRecentOutput()}", exception);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private static string SelectReasoningAlias(IReadOnlyList<string> visibleAliases)
    {
        var preferredMatch = PreferredReasoningAliases.FirstOrDefault(alias => visibleAliases.Contains(alias, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(preferredMatch))
        {
            return preferredMatch;
        }

        var heuristicMatch = visibleAliases.FirstOrDefault(static alias =>
            alias.Contains("reason", StringComparison.OrdinalIgnoreCase)
            || alias.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            || alias.Contains("r1", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(heuristicMatch))
        {
            return heuristicMatch;
        }

        throw new InvalidOperationException(
            $"No reasoning-capable Foundry alias was visible for the browser regression. Visible aliases: {string.Join(", ", visibleAliases)}");
    }

    private static async Task RunWithTimeoutAsync(string stage, Func<Task> action, TimeSpan timeout)
    {
        try
        {
            await action().WaitAsync(timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Playwright reasoning-monitor stage '{stage}' exceeded {timeout}.", exception);
        }
    }
}