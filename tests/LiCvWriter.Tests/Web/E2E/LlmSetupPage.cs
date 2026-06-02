using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using Xunit.Sdk;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class LlmSetupPage(IPage page, string baseUrl)
{
    private readonly IPage page = page;
    private readonly string baseUrl = baseUrl.TrimEnd('/');

    private ILocator CatalogRows => page.Locator("table.setup-list-table tbody tr:has(input[type='checkbox'])");

    public ILocator ProviderSelect => page.Locator("#llmProvider");

    public ILocator FoundryModelFilter => page.Locator("#foundryModelFilter");

    public ILocator SelectVisibleUsableModelsButton => page.GetByRole(AriaRole.Button, new() { Name = "Select visible usable models" });

    public ILocator SelectNoneButton => page.GetByRole(AriaRole.Button, new() { Name = "Select none" });

    public ILocator RemoveSelectedCachedButton => page.GetByRole(AriaRole.Button, new() { Name = "Remove selected cached" });

    public ILocator StartBenchmarkButton => page.GetByRole(AriaRole.Button, new() { Name = "Benchmark selected" });

    public ILocator RunningBenchmarkButton => page.GetByRole(AriaRole.Button, new() { Name = "Benchmarking..." });

    public ILocator CancelBenchmarkButton => page.GetByRole(AriaRole.Button, new() { Name = "Cancel benchmark" });

    public ILocator BenchmarkSnapshotHeading => page.GetByText("Benchmark results");

    public ILocator ReasoningMonitor => page.Locator(".sidebar-crt-screen").First;

    public ILocator ActivityMonitor => page.Locator(".sidebar-crt-screen-activity").First;

    public async Task GotoAsync()
    {
        await page.GotoAsync($"{baseUrl}/setup/llm", new PageGotoOptions
        {
            Timeout = 60_000,
            WaitUntil = WaitUntilState.Commit
        });
        await page.WaitForTimeoutAsync(2_000);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "LLM selection and related work" })).ToBeVisibleAsync();
    }

    public async Task SelectFoundryProviderAsync()
    {
        await ProviderSelect.SelectOptionAsync("Foundry");
        await Expect(FoundryModelFilter).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 120_000
        });
    }

    public async Task SelectOllamaProviderAsync()
    {
        await ProviderSelect.SelectOptionAsync("Ollama");
        await Expect(page.GetByText("Ollama model installation and removal stay outside the app", new PageGetByTextOptions
        {
            Exact = false
        })).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 120_000
        });
    }

    public async Task FilterFoundryCatalogAsync(string filter)
    {
        await FoundryModelFilter.FillAsync(filter);
        await FoundryModelFilter.PressAsync("Tab");
    }

    public async Task SelectVisibleUsableModelsAsync()
    {
        await Expect(SelectVisibleUsableModelsButton).ToBeEnabledAsync();
        await SelectVisibleUsableModelsButton.ClickAsync();
    }

    public async Task ClearSelectedModelsAsync()
    {
        if (await SelectNoneButton.IsDisabledAsync())
        {
            return;
        }

        await SelectNoneButton.ClickAsync();
        await WaitForSelectedModelCountAsync(0);
    }

    public async Task SelectFoundryModelAsync(string alias)
        => await SelectCatalogModelAsync(alias);

    public async Task SelectOllamaModelAsync(string model)
        => await SelectCatalogModelAsync(model);

    private async Task SelectCatalogModelAsync(string modelName)
    {
        var row = CatalogRows.Filter(new LocatorFilterOptions
        {
            HasText = modelName
        }).First;
        var checkbox = row.Locator("input[type='checkbox']");

        await Expect(row).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 120_000
        });
        await checkbox.ClickAsync();
        await Expect(checkbox).ToBeCheckedAsync();
    }

    public async Task RemoveSelectedCachedAsync()
    {
        await Expect(RemoveSelectedCachedButton).ToBeEnabledAsync();
        await RemoveSelectedCachedButton.ClickAsync();
    }

    public async Task WaitForFoundryModelStatusAsync(string alias, string status, TimeSpan? timeout = null)
    {
        var row = CatalogRows.Filter(new LocatorFilterOptions
        {
            HasText = alias
        }).First;

        await Expect(row.Locator("td:last-child")).ToContainTextAsync(status, new LocatorAssertionsToContainTextOptions
        {
            Timeout = (float)(timeout ?? TimeSpan.FromMinutes(1)).TotalMilliseconds
        });
    }

    public async Task AssertFoundryModelSelectedAsync(string alias)
    {
        var row = CatalogRows.Filter(new LocatorFilterOptions
        {
            HasText = alias
        }).First;
        var checkbox = row.Locator("input[type='checkbox']");

        await Expect(checkbox).ToBeCheckedAsync(new LocatorAssertionsToBeCheckedOptions
        {
            Timeout = 30_000
        });
    }

    public async Task EnsureVisibleAliasesAsync(params string[] aliases)
    {
        var visibleAliases = await GetVisibleAliasesAsync();
        var missingAliases = aliases.Where(alias => !visibleAliases.Contains(alias, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (missingAliases.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The Setup / LLM page did not show the expected Foundry aliases: {string.Join(", ", missingAliases)}. Visible aliases: {string.Join(", ", visibleAliases)}");
    }

    public async Task StartBenchmarkAsync()
    {
        await Expect(StartBenchmarkButton).ToBeEnabledAsync();
        await StartBenchmarkButton.ClickAsync();
        await Expect(RunningBenchmarkButton).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 30_000
        });
    }

    public async Task CancelBenchmarkAsync()
    {
        await Expect(CancelBenchmarkButton).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 30_000
        });
        await CancelBenchmarkButton.ClickAsync();
    }

    public async Task WaitForBenchmarkCompletionAsync()
    {
        await Expect(StartBenchmarkButton).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 720_000
        });
        await Expect(BenchmarkSnapshotHeading).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 720_000
        });
    }

    public async Task WaitForReasoningMonitorToShowCapturedTextAsync(TimeSpan? timeout = null)
    {
        var resolvedTimeout = (float)(timeout ?? TimeSpan.FromMinutes(5)).TotalMilliseconds;

        await Expect(ReasoningMonitor).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = resolvedTimeout
        });

        await Expect(ReasoningMonitor).Not.ToContainTextAsync(
            "No reasoning text captured yet. Run a streamed LLM operation to light up this monitor.",
            new LocatorAssertionsToContainTextOptions
            {
                Timeout = resolvedTimeout
            });
    }

    public async Task<string> GetReasoningMonitorTextAsync()
        => FlattenWhitespace(await ReasoningMonitor.InnerTextAsync());

    public async Task WaitForActivityMonitorTelemetryAsync(string modelAlias, TimeSpan? timeout = null)
    {
        var resolvedTimeout = (float)(timeout ?? TimeSpan.FromMinutes(5)).TotalMilliseconds;

        await Expect(ActivityMonitor).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = resolvedTimeout
        });

        await Expect(ActivityMonitor).ToContainTextAsync($"MODEL : {modelAlias}", new LocatorAssertionsToContainTextOptions
        {
            Timeout = resolvedTimeout
        });

        await Expect(ActivityMonitor).ToContainTextAsync("TOKENS:", new LocatorAssertionsToContainTextOptions
        {
            Timeout = resolvedTimeout
        });
    }

    public async Task WaitForSelectedModelCountAsync(int count, TimeSpan? timeout = null)
    {
        var expectedPattern = count == 0
            ? new Regex("No models selected for batch actions\\.", RegexOptions.IgnoreCase)
            : new Regex($@"Selected models:\s*{count}\.", RegexOptions.IgnoreCase);

        await Expect(page.GetByText(expectedPattern)).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = (float)(timeout ?? TimeSpan.FromMinutes(1)).TotalMilliseconds
        });
    }

    public async Task WaitForLiveQueueTotalAsync(int totalCount, TimeSpan? timeout = null)
    {
        await Expect(page.Locator(".benchmark-live-rail").GetByText(
            new Regex($@"\b\d+\s*/\s*{totalCount}\s+complete\b", RegexOptions.IgnoreCase))).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = (float)(timeout ?? TimeSpan.FromMinutes(1)).TotalMilliseconds
            });
    }

    public async Task WaitForQueueProgressAsync(int completedCount, int totalCount, TimeSpan? timeout = null)
    {
        await Expect(page.Locator(".benchmark-live-rail").GetByText($"{completedCount} / {totalCount} complete")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = (float)(timeout ?? TimeSpan.FromMinutes(5)).TotalMilliseconds
        });
    }

    public async Task WaitForCurrentBenchmarkModelAsync(string modelAlias, TimeSpan? timeout = null)
    {
        var overallTimeout = timeout ?? TimeSpan.FromMinutes(30);
        var stallTimeout = TimeSpan.FromMinutes(4);
        var deadline = DateTimeOffset.UtcNow + overallTimeout;
        var lastProgressUtc = DateTimeOffset.UtcNow;
        string? lastSignature = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var currentModelText = await GetCurrentBenchmarkCardTextAsync();
            if (currentModelText.Contains(modelAlias, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var progressSignature = await GetLiveBenchmarkProgressSignatureAsync();
            var now = DateTimeOffset.UtcNow;

            if (!string.Equals(progressSignature, lastSignature, StringComparison.Ordinal))
            {
                lastSignature = progressSignature;
                lastProgressUtc = now;
            }
            else if (now - lastProgressUtc >= stallTimeout)
            {
                throw new TimeoutException(
                    $"Benchmark progress stalled for {stallTimeout} before current model reached '{modelAlias}'.{Environment.NewLine}"
                    + await GetBenchmarkProgressDiagnosticsAsync("Benchmark progress snapshot at stall timeout."));
            }

            await page.WaitForTimeoutAsync(5_000);
        }

        throw new TimeoutException(
            $"Benchmark did not reach current model '{modelAlias}' within {overallTimeout}.{Environment.NewLine}"
            + await GetBenchmarkProgressDiagnosticsAsync("Benchmark progress snapshot at overall timeout."));
    }

    public async Task<string> GetBenchmarkRowTextAsync(string modelAlias)
    {
        var row = page.Locator(".benchmark-summary-shell + table tbody tr").Filter(new LocatorFilterOptions
        {
            HasText = modelAlias
        }).First;

        await Expect(row).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 60_000
        });
        return await row.InnerTextAsync();
    }

    public async Task<string> GetBenchmarkRowDiagnosticsTextAsync(string modelAlias)
    {
        var row = page.Locator(".benchmark-summary-shell + table tbody tr").Filter(new LocatorFilterOptions
        {
            HasText = modelAlias
        }).First;

        await Expect(row).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 60_000
        });

        var diagnostics = row.Locator(".benchmark-row-diagnostics").First;
        await Expect(diagnostics).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 60_000
        });

        return FlattenWhitespace(await diagnostics.InnerTextAsync());
    }

    public async Task<string> GetLiveBenchmarkDiagnosticsCardTextAsync()
    {
        var diagnosticsCard = page.Locator(".benchmark-live-card").Filter(new LocatorFilterOptions
        {
            HasText = "Diagnostics"
        }).First;

        await Expect(diagnosticsCard).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 60_000
        });

        return FlattenWhitespace(await diagnosticsCard.InnerTextAsync());
    }

    public async Task WaitForLiveBenchmarkDiagnosticsAsync(string expectedText, TimeSpan? timeout = null)
    {
        var diagnosticsCard = page.Locator(".benchmark-live-card").Filter(new LocatorFilterOptions
        {
            HasText = "Diagnostics"
        }).First;

        await Expect(diagnosticsCard).ToContainTextAsync(expectedText, new LocatorAssertionsToContainTextOptions
        {
            Timeout = (float)(timeout ?? TimeSpan.FromMinutes(5)).TotalMilliseconds
        });
    }

    public async Task<string> GetBenchmarkDiagnosticsAsync()
    {
        var bodyText = await page.Locator("body").InnerTextAsync();
        return await BuildBenchmarkDiagnosticsAsync(bodyText, "Disposed runtime text surfaced on the Setup / LLM page.");
    }

    public async Task<string> GetBenchmarkProgressDiagnosticsAsync(string heading)
    {
        var bodyText = await page.Locator("body").InnerTextAsync();
        return await BuildBenchmarkDiagnosticsAsync(bodyText, heading);
    }

    public async Task<IReadOnlyList<string>> GetVisibleAliasesAsync()
    {
        var aliasCells = CatalogRows.Locator("td:nth-child(3)");
        var aliases = await aliasCells.AllInnerTextsAsync();
        return aliases
            .Select(static alias => alias.Trim())
            .Where(static alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static alias => alias, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> GetVisibleOllamaModelsAsync()
    {
        var modelCells = CatalogRows.Locator("td:nth-child(2)");
        var models = await modelCells.AllInnerTextsAsync();
        return models
            .Select(static model => model.Trim())
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task AssertNoDisposedRuntimeErrorsAsync()
    {
        var bodyText = await page.Locator("body").InnerTextAsync();
        if (bodyText.Contains("Cannot access a disposed object", StringComparison.OrdinalIgnoreCase)
            || bodyText.Contains("SemaphoreSlim", StringComparison.OrdinalIgnoreCase))
        {
            throw new XunitException(await BuildBenchmarkDiagnosticsAsync(bodyText, "Disposed runtime text surfaced on the Setup / LLM page."));
        }
    }

    private async Task<string> BuildBenchmarkDiagnosticsAsync(string bodyText, string heading)
    {
        var benchmarkRows = await GetBenchmarkRowsAsync();
        var diagnostics = new List<string>
        {
            heading
        };

        if (TryExtractQueueProgress(bodyText) is { } queueProgress)
        {
            diagnostics.Add($"Queue progress: {queueProgress}");
        }

        if (benchmarkRows.Count > 0)
        {
            diagnostics.Add("Benchmark rows:");
            diagnostics.AddRange(benchmarkRows.Select(static row => $"- {row}"));
        }

        diagnostics.Add($"Body excerpt: {ExtractDisposedErrorExcerpt(bodyText)}");
        return string.Join(Environment.NewLine, diagnostics);
    }

    private async Task<IReadOnlyList<string>> GetBenchmarkRowsAsync()
    {
        var rows = await page.Locator(".benchmark-summary-shell + table tbody tr").AllInnerTextsAsync();
        return rows
            .Select(FlattenWhitespace)
            .Where(static row => !string.IsNullOrWhiteSpace(row))
            .ToArray();
    }

    private async Task<string> GetCurrentBenchmarkCardTextAsync()
    {
        var currentModelCard = page.Locator(".benchmark-live-card").Filter(new LocatorFilterOptions
        {
            HasText = "Current model"
        }).First;

        await Expect(currentModelCard).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 30_000
        });

        return FlattenWhitespace(await currentModelCard.InnerTextAsync());
    }

    private async Task<string> GetLiveBenchmarkProgressSignatureAsync()
    {
        var liveRailText = FlattenWhitespace(await page.Locator(".benchmark-live-rail").InnerTextAsync());
        var benchmarkRows = await GetBenchmarkRowsAsync();

        return benchmarkRows.Count == 0
            ? liveRailText
            : string.Join(" || ", new[] { liveRailText }.Concat(benchmarkRows));
    }

    private static string? TryExtractQueueProgress(string bodyText)
    {
        var match = Regex.Match(bodyText, "\\b\\d+\\s*/\\s*\\d+\\s+complete\\b", RegexOptions.IgnoreCase);
        return match.Success ? FlattenWhitespace(match.Value) : null;
    }

    private static string ExtractDisposedErrorExcerpt(string bodyText)
    {
        var markerIndex = bodyText.IndexOf("Cannot access a disposed object", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            markerIndex = bodyText.IndexOf("SemaphoreSlim", StringComparison.OrdinalIgnoreCase);
        }

        if (markerIndex < 0)
        {
            return FlattenWhitespace(bodyText);
        }

        var start = Math.Max(0, markerIndex - 120);
        var length = Math.Min(bodyText.Length - start, 320);
        return FlattenWhitespace(bodyText.Substring(start, length));
    }

    private static string FlattenWhitespace(string value)
        => Regex.Replace(value, "\\s+", " ").Trim();
}