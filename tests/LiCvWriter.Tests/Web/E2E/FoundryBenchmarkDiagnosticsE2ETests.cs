using Microsoft.Playwright;
using Xunit.Sdk;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class FoundryBenchmarkDiagnosticsE2ETests(PlaywrightAppFixture fixture) : IClassFixture<PlaywrightAppFixture>
{
    private static readonly string[][] PreferredComparisonSets =
    [
        ["deepseek-r1-14b", "deepseek-r1-7b"],
        ["phi-4-mini", "phi-4"]
    ];

    [LivePlaywrightFact(2_100_000)]
    public async Task RunTargetedFoundryBenchmark_ShowsBrowserVisibleDiagnosticsForSelectedModels()
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

            var targetAliases = await RunWithTimeoutAsync("choose benchmark targets", async () =>
            {
                var visibleAliases = await setupPage.GetVisibleAliasesAsync();
                return SelectTargetAliases(visibleAliases);
            }, TimeSpan.FromMinutes(2));

            foreach (var alias in targetAliases)
            {
                await RunWithTimeoutAsync($"select {alias}", () => setupPage.SelectFoundryModelAsync(alias), TimeSpan.FromMinutes(1));
            }

            await RunWithTimeoutAsync("verify selected target count", () => setupPage.WaitForSelectedModelCountAsync(targetAliases.Length, TimeSpan.FromMinutes(1)), TimeSpan.FromMinutes(2));
            await RunWithTimeoutAsync("start benchmark", setupPage.StartBenchmarkAsync, TimeSpan.FromMinutes(1));
            await RunWithTimeoutAsync("show live queue", () => setupPage.WaitForLiveQueueTotalAsync(targetAliases.Length, TimeSpan.FromMinutes(1)), TimeSpan.FromMinutes(2));
            await RunWithTimeoutAsync("show diagnostics card", async () =>
            {
                await setupPage.WaitForLiveBenchmarkDiagnosticsAsync("Diagnostics", TimeSpan.FromMinutes(2));
                await setupPage.WaitForLiveBenchmarkDiagnosticsAsync("Acceleration", TimeSpan.FromMinutes(2));
                var liveDiagnostics = await setupPage.GetLiveBenchmarkDiagnosticsCardTextAsync();
                Assert.Contains("Acceleration", liveDiagnostics, StringComparison.OrdinalIgnoreCase);
            }, TimeSpan.FromMinutes(3));

            var liveDiagnosticsDuringRun = await setupPage.GetLiveBenchmarkDiagnosticsCardTextAsync();
            Assert.True(
                ContainsExpectedDiagnosticsEvidence(liveDiagnosticsDuringRun),
                $"Expected browser-visible live diagnostics for '{string.Join(", ", targetAliases)}', but got: {liveDiagnosticsDuringRun}");

            await RunWithTimeoutAsync("cancel benchmark after diagnostics capture", setupPage.CancelBenchmarkAsync, TimeSpan.FromMinutes(1));
            await RunWithTimeoutAsync("finalize cancelled benchmark", setupPage.WaitForBenchmarkCompletionAsync, TimeSpan.FromMinutes(10));
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private static string[] SelectTargetAliases(IReadOnlyList<string> visibleAliases)
    {
        foreach (var comparisonSet in PreferredComparisonSets)
        {
            var matchedSet = comparisonSet
                .Select(alias => visibleAliases.FirstOrDefault(visible => visible.Contains(alias, StringComparison.OrdinalIgnoreCase)))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (matchedSet.Length != comparisonSet.Length)
            {
                continue;
            }

            return matchedSet;
        }

        throw new XunitException(
            $"Could not find a supported Foundry comparison set for the diagnostics harness. Visible aliases: {string.Join(", ", visibleAliases)}");
    }

    private static bool ContainsExpectedDiagnosticsEvidence(string diagnostics)
        => diagnostics.Contains("prepare", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("warm-up", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("evaluate", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("acceleration", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("CPU only", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("GPU", StringComparison.OrdinalIgnoreCase);

    private static async Task RunWithTimeoutAsync(string stage, Func<Task> action, TimeSpan timeout)
    {
        try
        {
            await action().WaitAsync(timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Playwright diagnostics stage '{stage}' exceeded {timeout}.", exception);
        }
    }

    private static async Task<T> RunWithTimeoutAsync<T>(string stage, Func<Task<T>> action, TimeSpan timeout)
    {
        try
        {
            return await action().WaitAsync(timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Playwright diagnostics stage '{stage}' exceeded {timeout}.", exception);
        }
    }
}