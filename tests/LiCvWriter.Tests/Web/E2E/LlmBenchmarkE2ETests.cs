using Microsoft.Playwright;
using Xunit.Sdk;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class LlmBenchmarkE2ETests(PlaywrightAppFixture fixture) : IClassFixture<PlaywrightAppFixture>
{
    [LivePlaywrightFact]
    public async Task RunSelectedFoundryBenchmark_AfterRuntimeFailure_DoesNotCascadeDisposedRuntimeErrors()
    {
        var targetAliases = new[]
        {
            "mistral-nemo-12b-instruct",
            "olmo-3-7b-instruct",
            "phi-3-mini-128k",
            "phi-3-mini-4k",
            "phi-3.5-mini"
        };

        var context = await fixture.CreateContextAsync(recordVideo: false);
        IPage? page = null;

        try
        {
            page = await context.NewPageAsync();
            var setupPage = new LlmSetupPage(page, fixture.BaseUrl);

            await RunWithTimeoutAsync("open llm setup", setupPage.GotoAsync, TimeSpan.FromMinutes(2));
            await RunWithTimeoutAsync("load foundry catalog", setupPage.SelectFoundryProviderAsync, TimeSpan.FromMinutes(3));
            await RunWithTimeoutAsync("verify target aliases", async () =>
            {
                await setupPage.EnsureVisibleAliasesAsync(targetAliases);
            }, TimeSpan.FromMinutes(2));
            await RunWithTimeoutAsync("select benchmark targets", async () =>
            {
                foreach (var alias in targetAliases)
                {
                    await setupPage.SelectFoundryModelAsync(alias);
                }
            }, TimeSpan.FromMinutes(3));
            await RunWithTimeoutAsync("run foundry benchmark past prior cascade point", async () =>
            {
                await setupPage.StartBenchmarkAsync();
                await setupPage.WaitForQueueProgressAsync(4, targetAliases.Length);
            }, TimeSpan.FromMinutes(6));

            var mini4kRow = await setupPage.GetBenchmarkRowTextAsync("phi-3-mini-4k");
            var benchmarkDiagnostics = await setupPage.GetBenchmarkDiagnosticsAsync();

            await setupPage.AssertNoDisposedRuntimeErrorsAsync();
            AssertRowDoesNotContainDisposedRuntimeText("phi-3-mini-4k", mini4kRow, benchmarkDiagnostics);
        }
        finally
        {
            await context.CloseAsync();
        }
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