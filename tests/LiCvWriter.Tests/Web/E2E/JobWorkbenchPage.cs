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

    public ILocator StatusMonitor => page.Locator(".sidebar-status-crt-screen");

    public ILocator ReasoningMonitor => page.Locator(".sidebar-crt-screen").First;

    public ILocator ActivityFeed => page.Locator(".activity-feed");

    public ILocator ActivityEntries => page.Locator(".activity-entry");

    public ILocator RunningRows => page.Locator(".overview-row .status-chip.status-pending");

    public ILocator BatchStatusText => page.GetByText(new Regex("Batch:\\s*[0-3] of 3", RegexOptions.IgnoreCase));

    public async Task GotoAsync()
    {
        await page.GotoAsync($"{baseUrl}/workspace/job-workbench", new PageGotoOptions
        {
            Timeout = 60_000,
            WaitUntil = WaitUntilState.Commit
        });
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Job Workbench" })).ToBeVisibleAsync();
        await Expect(JobRows).ToHaveCountAsync(3);
        await Expect(JobRowTitles).ToHaveCountAsync(3);
        await Expect(JobRowTitles.First).ToBeVisibleAsync();
    }

    public async Task SelectFirstJobSetsAsync(int count)
    {
        var checkboxes = page.Locator(".overview-row input[type='checkbox']");
        await Expect(checkboxes).ToHaveCountAsync(3);

        for (var index = 0; index < count; index++)
        {
            await checkboxes.Nth(index).CheckAsync();
        }

        await Expect(SelectedJobCheckboxes).ToHaveCountAsync(count);
    }

    public async Task SelectJobSetAsync(int index)
    {
        var checkboxes = page.Locator(".overview-row input[type='checkbox']");
        await Expect(checkboxes).ToHaveCountAsync(3);

        await JobRows.Nth(index).ScrollIntoViewIfNeededAsync();
        await checkboxes.Nth(index).CheckAsync();
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

    public async Task FocusActivityFeedAsync()
    {
        await ActivityFeed.ScrollIntoViewIfNeededAsync();
        await ActivityFeed.HoverAsync();
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

        await Expect(StatusMonitor).Not.ToContainTextAsync("No streaming status captured yet", new LocatorAssertionsToContainTextOptions
        {
            Timeout = 180_000
        });
    }

    public async Task WaitForActivityEntryAsync()
    {
        await Expect(ActivityEntries.First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 420_000
        });
    }
}
