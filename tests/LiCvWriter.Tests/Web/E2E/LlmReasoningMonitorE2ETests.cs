using Microsoft.Playwright;
using Xunit.Sdk;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class LlmReasoningMonitorE2ETests(PlaywrightAppFixture fixture) : IClassFixture<PlaywrightAppFixture>
{
    private static readonly string[] Phi4Aliases = ["phi-4", "phi-4-reasoning"];

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

    [LivePlaywrightFact(1_800_000)]
    public async Task RunSingleFoundryBenchmark_WhenPhi4AliasesAreVisible_DoesNotLeavePathologicalRepetitionInReasoningMonitor()
    {
        var probeContext = await fixture.CreateContextAsync(recordVideo: false);
        try
        {
            var probePage = await probeContext.NewPageAsync();
            var probeSetupPage = new LlmSetupPage(probePage, fixture.BaseUrl);

            await RunWithTimeoutAsync("open llm setup", probeSetupPage.GotoAsync, TimeSpan.FromMinutes(2));
            await RunWithTimeoutAsync("load foundry catalog", probeSetupPage.SelectFoundryProviderAsync, TimeSpan.FromMinutes(3));

            var visibleAliases = await probeSetupPage.GetVisibleAliasesAsync();
            var targets = Phi4Aliases.Where(alias => visibleAliases.Contains(alias, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (targets.Length == 0)
            {
                return;
            }

            foreach (var targetAlias in targets)
            {
                var context = await fixture.CreateContextAsync(recordVideo: false);
                try
                {
                    var page = await context.NewPageAsync();
                    var setupPage = new LlmSetupPage(page, fixture.BaseUrl);

                    await RunWithTimeoutAsync($"open llm setup for {targetAlias}", setupPage.GotoAsync, TimeSpan.FromMinutes(2));
                    await RunWithTimeoutAsync($"load foundry catalog for {targetAlias}", setupPage.SelectFoundryProviderAsync, TimeSpan.FromMinutes(3));
                    await RunWithTimeoutAsync($"clear prior model selection for {targetAlias}", setupPage.ClearSelectedModelsAsync, TimeSpan.FromMinutes(1));
                    await RunWithTimeoutAsync($"select {targetAlias}", () => setupPage.SelectFoundryModelAsync(targetAlias), TimeSpan.FromMinutes(2));
                    await RunWithTimeoutAsync($"verify single selected model for {targetAlias}", () => setupPage.WaitForSelectedModelCountAsync(1, TimeSpan.FromMinutes(1)), TimeSpan.FromMinutes(2));
                    await RunWithTimeoutAsync($"start reasoning benchmark for {targetAlias}", setupPage.StartBenchmarkAsync, TimeSpan.FromMinutes(1));
                    await RunWithTimeoutAsync($"show activity monitor telemetry for {targetAlias}", () => setupPage.WaitForActivityMonitorTelemetryAsync(targetAlias, TimeSpan.FromMinutes(8)), TimeSpan.FromMinutes(9));
                    await RunWithTimeoutAsync($"show reasoning monitor text for {targetAlias}", () => setupPage.WaitForReasoningMonitorToShowCapturedTextAsync(TimeSpan.FromMinutes(8)), TimeSpan.FromMinutes(9));
                    await RunWithTimeoutAsync(
                        $"observe reasoning monitor stability for {targetAlias}",
                        () => AssertReasoningMonitorStaysStableAsync(page, setupPage, targetAlias),
                        TimeSpan.FromMinutes(3));
                }
                finally
                {
                    await context.CloseAsync();
                }
            }
        }
        finally
        {
            await probeContext.CloseAsync();
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

    private async Task AssertReasoningMonitorStaysStableAsync(IPage page, LlmSetupPage setupPage, string targetAlias)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        string latestReasoningText = string.Empty;

        while (DateTimeOffset.UtcNow < deadline)
        {
            latestReasoningText = await setupPage.GetReasoningMonitorTextAsync();
            if (ContainsPathologicalRepetition(latestReasoningText, out var repeatedFragment))
            {
                throw new XunitException(
                    $"Reasoning monitor showed pathological repetition for {targetAlias}. Fragment: '{repeatedFragment}'.{Environment.NewLine}" +
                    $"Reasoning monitor: {latestReasoningText}{Environment.NewLine}{Environment.NewLine}" +
                    $"App output:{Environment.NewLine}{fixture.GetRecentOutput()}");
            }

            await page.WaitForTimeoutAsync(5_000);
        }
    }

    private static bool ContainsPathologicalRepetition(string text, out string repeatedFragment)
    {
        repeatedFragment = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = string.Join(' ', text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 8)
        {
            return false;
        }

        for (var phraseLength = 1; phraseLength <= 3; phraseLength++)
        {
            for (var start = 0; start <= tokens.Length - phraseLength * 6; start++)
            {
                var phrase = string.Join(' ', tokens.Skip(start).Take(phraseLength));
                if (phrase.All(static character => !char.IsLetterOrDigit(character)))
                {
                    continue;
                }

                var repeats = 1;
                while (start + (repeats + 1) * phraseLength <= tokens.Length)
                {
                    var candidate = string.Join(' ', tokens.Skip(start + repeats * phraseLength).Take(phraseLength));
                    if (!candidate.Equals(phrase, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    repeats++;
                }

                if (repeats >= 6)
                {
                    repeatedFragment = phrase;
                    return true;
                }
            }
        }

        return false;
    }
}