using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using Xunit.Sdk;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class LlmSetupPage(IPage page, string baseUrl)
{
    private readonly IPage page = page;
    private readonly string baseUrl = baseUrl.TrimEnd('/');

    public ILocator ProviderSelect => page.Locator("#llmProvider");

    public ILocator FoundryModelFilter => page.Locator("#foundryModelFilter");

    public ILocator SelectAllVisibleButton => page.GetByRole(AriaRole.Button, new() { Name = "Select all visible" });

    public ILocator StartBenchmarkButton => page.GetByRole(AriaRole.Button, new() { Name = "Benchmark selected" });

    public ILocator RunningBenchmarkButton => page.GetByRole(AriaRole.Button, new() { Name = "Benchmarking..." });

    public ILocator BenchmarkSnapshotHeading => page.GetByText("Benchmark snapshot");

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

    public async Task FilterFoundryCatalogAsync(string filter)
    {
        await FoundryModelFilter.FillAsync(filter);
        await FoundryModelFilter.PressAsync("Tab");
    }

    public async Task SelectAllVisibleModelsAsync()
    {
        await Expect(SelectAllVisibleButton).ToBeEnabledAsync();
        await SelectAllVisibleButton.ClickAsync();
    }

    public async Task SelectFoundryModelAsync(string alias)
    {
        var row = page.Locator($"tbody tr:has-text('{alias}')").First;
        var checkbox = row.Locator("input[type='checkbox']");

        await Expect(row).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 120_000
        });
        await checkbox.CheckAsync();
        await Expect(checkbox).ToBeCheckedAsync();
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

    public async Task WaitForQueueProgressAsync(int completedCount, int totalCount)
    {
        await Expect(page.GetByText($"{completedCount} / {totalCount} complete")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 300_000
        });
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

    public async Task<string> GetBenchmarkDiagnosticsAsync()
    {
        var bodyText = await page.Locator("body").InnerTextAsync();
        return await BuildBenchmarkDiagnosticsAsync(bodyText);
    }

    public async Task<IReadOnlyList<string>> GetVisibleAliasesAsync()
    {
        var aliasCells = page.Locator(".setup-list-table tbody tr td:nth-child(3)");
        var aliases = await aliasCells.AllInnerTextsAsync();
        return aliases
            .Select(static alias => alias.Trim())
            .Where(static alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static alias => alias, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task AssertNoDisposedRuntimeErrorsAsync()
    {
        var bodyText = await page.Locator("body").InnerTextAsync();
        if (bodyText.Contains("Cannot access a disposed object", StringComparison.OrdinalIgnoreCase)
            || bodyText.Contains("SemaphoreSlim", StringComparison.OrdinalIgnoreCase))
        {
            throw new XunitException(await BuildBenchmarkDiagnosticsAsync(bodyText));
        }
    }

    private async Task<string> BuildBenchmarkDiagnosticsAsync(string bodyText)
    {
        var benchmarkRows = await GetBenchmarkRowsAsync();
        var diagnostics = new List<string>
        {
            "Disposed runtime text surfaced on the Setup / LLM page."
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