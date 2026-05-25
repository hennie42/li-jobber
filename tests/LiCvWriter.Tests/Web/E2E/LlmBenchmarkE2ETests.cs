using Microsoft.Playwright;
using Xunit.Sdk;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class LlmBenchmarkE2ETests(PlaywrightAppFixture fixture) : IClassFixture<PlaywrightAppFixture>
{
    private static readonly string[] PreferredBenchmarkAliases =
    [
        "mistral-nemo-12b-instruct",
        "olmo-3-7b-instruct",
        "phi-3-mini-128k",
        "phi-3-mini-4k",
        "phi-3.5-mini",
        "phi-4-mini",
        "gemma-3-4b-it",
        "qwen2.5-7b-instruct"
    ];

    [LivePlaywrightFact(2_100_000)]
    public async Task RunSelectedFoundryBenchmark_AfterRuntimeFailure_DoesNotCascadeDisposedRuntimeErrors()
    {
        string[] targetAliases = [];
        string[] queuedAliases = [];
        string lateQueueAlias = string.Empty;
        string completedDeepQueueAlias = string.Empty;

        var context = await fixture.CreateContextAsync(recordVideo: false);
        IPage? page = null;

        try
        {
            page = await context.NewPageAsync();
            var setupPage = new LlmSetupPage(page, fixture.BaseUrl);

            await RunWithTimeoutAsync("open llm setup", setupPage.GotoAsync, TimeSpan.FromMinutes(2));
            await RunWithTimeoutAsync("load foundry catalog", setupPage.SelectFoundryProviderAsync, TimeSpan.FromMinutes(3));
            await RunWithTimeoutAsync("clear prior model selection", setupPage.ClearSelectedModelsAsync, TimeSpan.FromMinutes(1));
            await RunWithTimeoutAsync("choose 8 benchmark targets", async () =>
            {
                targetAliases = SelectBenchmarkAliases(await setupPage.GetVisibleAliasesAsync());
                queuedAliases = targetAliases
                    .OrderBy(static alias => alias, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                lateQueueAlias = queuedAliases[6];
                completedDeepQueueAlias = queuedAliases[5];
            }, TimeSpan.FromMinutes(2));
            await RunWithTimeoutAsync("select benchmark targets", async () =>
            {
                foreach (var alias in targetAliases)
                {
                    await setupPage.SelectFoundryModelAsync(alias);
                }
            }, TimeSpan.FromMinutes(3));
            await RunWithTimeoutAsync("verify selected benchmark targets", async () =>
            {
                await setupPage.WaitForSelectedModelCountAsync(targetAliases.Length, TimeSpan.FromMinutes(1));
                foreach (var alias in targetAliases)
                {
                    await setupPage.AssertFoundryModelSelectedAsync(alias);
                }
            }, TimeSpan.FromMinutes(2));
            await RunWithTimeoutAsync("start benchmark", setupPage.StartBenchmarkAsync, TimeSpan.FromMinutes(1));
            await RunWithTimeoutAsync("show live queue for benchmark run", () => setupPage.WaitForLiveQueueTotalAsync(targetAliases.Length, TimeSpan.FromMinutes(1)), TimeSpan.FromMinutes(2));
            await WaitForLateQueuedModelAsync(setupPage, lateQueueAlias, TimeSpan.FromMinutes(30));

            await setupPage.AssertNoDisposedRuntimeErrorsAsync();
            var benchmarkDiagnostics = await setupPage.GetBenchmarkDiagnosticsAsync();
            var completedDeepQueueRow = await setupPage.GetBenchmarkRowTextAsync(completedDeepQueueAlias);

            AssertRowDoesNotContainDisposedRuntimeText(completedDeepQueueAlias, completedDeepQueueRow, benchmarkDiagnostics);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private static string[] SelectBenchmarkAliases(IReadOnlyList<string> visibleAliases)
    {
        var selectedAliases = PreferredBenchmarkAliases
            .Where(alias => visibleAliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
            .Concat(visibleAliases.Where(alias => !PreferredBenchmarkAliases.Contains(alias, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

        if (selectedAliases.Length == 8)
        {
            return selectedAliases;
        }

        throw new XunitException(
            $"Expected at least 8 visible Foundry aliases for the late-queue regression, but found {visibleAliases.Count}: {string.Join(", ", visibleAliases)}");
    }

    private static async Task RunWithTimeoutAsync(string stage, Func<Task> action, TimeSpan timeout)
    {
        try
        {
            await action().WaitAsync(timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Playwright benchmark stage '{stage}' exceeded {timeout}.", exception);
        }
    }

    private async Task WaitForLateQueuedModelAsync(LlmSetupPage setupPage, string modelAlias, TimeSpan timeout)
    {
        try
        {
            await setupPage.WaitForCurrentBenchmarkModelAsync(modelAlias, timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Playwright benchmark stage 'reach late queued model without disposed-runtime cascade' failed.{Environment.NewLine}"
                + $"{exception.Message}{Environment.NewLine}{Environment.NewLine}"
                + $"App output:{Environment.NewLine}{fixture.GetRecentOutput()}",
                exception);
        }
    }

    private static void AssertRowDoesNotContainDisposedRuntimeText(string alias, string rowText, string benchmarkDiagnostics)
    {
        if (rowText.Contains("SemaphoreSlim", StringComparison.OrdinalIgnoreCase)
            || rowText.Contains("disposed object", StringComparison.OrdinalIgnoreCase))
        {
            throw new XunitException(
                $"Benchmark row for '{alias}' contains disposed-runtime text.{Environment.NewLine}"
                + $"Row: {rowText}{Environment.NewLine}"
                + benchmarkDiagnostics);
        }
    }
}