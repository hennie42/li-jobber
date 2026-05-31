using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class LinkedInSetupPage(IPage page, string baseUrl)
{
    private readonly IPage page = page;
    private readonly string baseUrl = baseUrl.TrimEnd('/');

    public ILocator SnapshotSummary => page.Locator("summary").Filter(new LocatorFilterOptions
    {
        HasText = "Snapshot domains"
    });

    public async Task GotoAsync()
    {
        await page.GotoAsync($"{baseUrl}/setup/linkedin", new PageGotoOptions
        {
            Timeout = 60_000,
            WaitUntil = WaitUntilState.Commit
        });

        await page.WaitForTimeoutAsync(2_000);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "LinkedIn-related" })).ToBeVisibleAsync();
        await Expect(SnapshotSummary).ToBeVisibleAsync();
    }

    public ILocator DomainCheckbox(string label)
        => page.GetByRole(AriaRole.Checkbox, new PageGetByRoleOptions
        {
            NameRegex = new Regex($"^{Regex.Escape(label)}(?:\\s|$)", RegexOptions.IgnoreCase)
        });

    public async Task SetDomainSelectedAsync(string label, bool isSelected)
    {
        var checkbox = DomainCheckbox(label);
        if (isSelected)
        {
            await checkbox.CheckAsync();
            await Expect(checkbox).ToBeCheckedAsync();
            return;
        }

        await checkbox.UncheckAsync();
        await Expect(checkbox).Not.ToBeCheckedAsync();
    }

    public async Task WaitForSelectedCountAsync(int count)
    {
        await Expect(SnapshotSummary).ToContainTextAsync($"Snapshot domains ({count} selected)");
    }

    public async Task ReloadAsync()
    {
        await page.ReloadAsync(new PageReloadOptions
        {
            Timeout = 60_000,
            WaitUntil = WaitUntilState.Commit
        });

        await page.WaitForTimeoutAsync(2_000);
        await Expect(SnapshotSummary).ToBeVisibleAsync();
    }

    public async Task AssertDomainSelectedAsync(string label, bool isSelected)
    {
        var checkbox = DomainCheckbox(label);
        if (isSelected)
        {
            await Expect(checkbox).ToBeCheckedAsync();
            return;
        }

        await Expect(checkbox).Not.ToBeCheckedAsync();
    }
}