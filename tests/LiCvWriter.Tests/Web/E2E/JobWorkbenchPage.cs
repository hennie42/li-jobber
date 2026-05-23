using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class JobWorkbenchPage(IPage page, string baseUrl)
{
    private readonly IPage page = page;
    private readonly string baseUrl = baseUrl.TrimEnd('/');

    public ILocator JobRows => page.Locator(".overview-row");

    public ILocator JobRowTitles => page.Locator(".overview-row-title");

    public ILocator JobRowSelectionLabels => page.Locator(".overview-row label.checkbox-row");

    public ILocator JobRowStatuses => page.Locator(".overview-row-status");

    public ILocator SelectedJobCheckboxes => page.Locator(".overview-row input[type='checkbox']:checked");

    public ILocator StartSelectedButton => page.GetByRole(AriaRole.Button, new() { Name = "Start selected" });

    public ILocator BatchButton => page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Batch ", RegexOptions.IgnoreCase) });

    public ILocator StatusMonitor => BatchStatusText;

    public ILocator ReasoningMonitor => page.Locator(".sidebar-crt-screen").First;

    public ILocator ActivityMonitor => page.Locator(".sidebar-crt-screen-activity").First;

    public ILocator RunningRows => page.Locator(".overview-row .status-chip.status-pending");

    public ILocator BatchStatusText => page.GetByText(new Regex("Batch:\\s*[0-3] of 3", RegexOptions.IgnoreCase));

    public async Task GotoAsync()
    {
        await page.GotoAsync($"{baseUrl}/workspace/job-workbench", new PageGotoOptions
        {
            Timeout = 60_000,
            WaitUntil = WaitUntilState.Commit
        });
        await page.WaitForTimeoutAsync(2_000);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Job Workbench" })).ToBeVisibleAsync();
        await Expect(JobRows).ToHaveCountAsync(3);
        await Expect(JobRowTitles).ToHaveCountAsync(3);
        await Expect(JobRowTitles.First).ToBeVisibleAsync();
    }

    public async Task SelectFirstJobSetsAsync(int count)
    {
        await Expect(JobRowTitles).ToHaveCountAsync(3);
        var titles = (await JobRowTitles.AllInnerTextsAsync())
            .Select(static title => title.Trim())
            .Take(count)
            .ToArray();

        for (var index = 0; index < count; index++)
        {
            await SelectJobSetAsync(titles[index]);
            await WaitForSelectedCountAsync(index + 1);
        }
    }

    public async Task SelectJobSetAsync(int index)
    {
        await Expect(JobRowTitles).ToHaveCountAsync(3);
        var title = (await JobRowTitles.Nth(index).InnerTextAsync()).Trim();
        await SelectJobSetAsync(title);
    }

    private async Task SelectJobSetAsync(string title)
    {
        var row = JobRows.Filter(new LocatorFilterOptions
        {
            Has = page.GetByRole(AriaRole.Link, new() { Name = title })
        });
        var selectionLabel = row.Locator("label.checkbox-row");
        var checkbox = row.Locator("input[type='checkbox']");

        await Expect(row).ToHaveCountAsync(1);
        await Expect(selectionLabel).ToBeVisibleAsync();
        await selectionLabel.ClickAsync();
        await Expect(checkbox).ToBeCheckedAsync();
    }

    private async Task WaitForSelectedCountAsync(int expectedCount)
    {
        // Blazor Server can briefly show the browser's local checkbox state before
        // the server event round-trip settles and re-renders the row list.
        try
        {
            await page.WaitForFunctionAsync(
                """
                expectedCount => {
                    const stateKey = "__liCvBatchSelectionState";
                    const state = window[stateKey] ??= { count: -1, stableAt: performance.now() };
                    const count = document.querySelectorAll(".overview-row input[type='checkbox']:checked").length;

                    if (state.count !== count) {
                        state.count = count;
                        state.stableAt = performance.now();
                    }

                    return count === expectedCount && (performance.now() - state.stableAt) >= 250;
                }
                """,
                expectedCount,
                new PageWaitForFunctionOptions
                {
                    Timeout = 30_000
                });
        }
        catch (TimeoutException exception)
        {
            var rowStates = await page.EvaluateAsync<string>(
                """
                () => JSON.stringify([...document.querySelectorAll('.overview-row')].map((row, index) => ({
                    index,
                    title: row.querySelector('.overview-row-title')?.textContent?.trim() ?? '',
                    checked: row.querySelector("input[type='checkbox']")?.checked ?? false
                })))
                """);

            throw new TimeoutException($"Timed out waiting for {expectedCount} selected job sets. Current row states: {rowStates}", exception);
        }
    }

    public async Task HoverJobSetAsync(int index)
    {
        var title = JobRowTitles.Nth(index);
        await title.ScrollIntoViewIfNeededAsync();
        await title.HoverAsync();
    }

    public async Task HoverStartSelectedAsync()
    {
        await StartSelectedButton.ScrollIntoViewIfNeededAsync();
        await StartSelectedButton.HoverAsync();
    }

    public async Task FocusStatusMonitorAsync()
    {
        await StatusMonitor.ScrollIntoViewIfNeededAsync();
        await StatusMonitor.HoverAsync();
    }

    public async Task FocusReasoningMonitorAsync()
    {
        await ReasoningMonitor.ScrollIntoViewIfNeededAsync();
        await ReasoningMonitor.HoverAsync();
    }

    public async Task FocusActivityMonitorAsync()
    {
        await ActivityMonitor.ScrollIntoViewIfNeededAsync();
        await ActivityMonitor.HoverAsync();
    }

    public async Task ScrollWorkbenchToTopAsync()
        => await page.EvaluateAsync("() => window.scrollTo({ top: 0, behavior: 'smooth' })");

    public async Task StartSelectedAsync()
    {
        await Expect(StartSelectedButton).ToBeEnabledAsync();
        await StartSelectedButton.ClickAsync();
        await Expect(BatchButton).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
    }

    public async Task WaitForLiveLlmProgressAsync()
    {
        await Expect(BatchButton).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 30_000
        });

        await Expect(BatchStatusText).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 30_000
        });

        await Expect(ActivityMonitor).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 180_000
        });
    }
}
