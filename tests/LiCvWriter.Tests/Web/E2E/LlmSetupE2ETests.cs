using Microsoft.Playwright;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class LlmSetupE2ETests(PlaywrightAppFixture fixture) : IClassFixture<PlaywrightAppFixture>
{
    [LivePlaywrightFact]
    public async Task RemoveSelectedCached_WhenPlaywrightFoundryDemoSeedsCachedAlias_UpdatesCatalogStatus()
    {
        await fixture.SeedDemoAsync("foundry-setup-remove");
        var context = await fixture.CreateContextAsync(recordVideo: false);
        IPage? page = null;

        try
        {
            await RunWithTimeoutAsync("create browser page", async () => page = await context.NewPageAsync(), TimeSpan.FromSeconds(30));
            if (page is null)
            {
                throw new InvalidOperationException("Playwright did not create a browser page for the LLM setup E2E test.");
            }

            var setupPage = new LlmSetupPage(page, fixture.BaseUrl);

            await RunWithTimeoutAsync("open llm setup", setupPage.GotoAsync, TimeSpan.FromMinutes(2));
            await RunWithTimeoutAsync("switch to foundry", setupPage.SelectFoundryProviderAsync, TimeSpan.FromMinutes(1));
            await RunWithTimeoutAsync("filter demo aliases", () => setupPage.FilterFoundryCatalogAsync("playwright-"), TimeSpan.FromSeconds(30));
            await RunWithTimeoutAsync(
                "verify demo aliases",
                () => setupPage.EnsureVisibleAliasesAsync("playwright-session", "playwright-removable", "playwright-downloadable"),
                TimeSpan.FromSeconds(30));
            await RunWithTimeoutAsync("select cached demo alias", () => setupPage.SelectFoundryModelAsync("playwright-removable"), TimeSpan.FromSeconds(30));
            await RunWithTimeoutAsync("wait for selected count", () => setupPage.WaitForSelectedModelCountAsync(1), TimeSpan.FromSeconds(30));
            await RunWithTimeoutAsync("remove selected cached alias", setupPage.RemoveSelectedCachedAsync, TimeSpan.FromSeconds(30));
            await RunWithTimeoutAsync(
                "wait for removable alias to become downloadable",
                () => setupPage.WaitForFoundryModelStatusAsync("playwright-removable", "Available to download", TimeSpan.FromSeconds(30)),
                TimeSpan.FromSeconds(45));
            await RunWithTimeoutAsync("wait for selection clear", () => setupPage.WaitForSelectedModelCountAsync(0, TimeSpan.FromSeconds(30)), TimeSpan.FromSeconds(45));
            await RunWithTimeoutAsync(
                "keep session alias cached",
                () => setupPage.WaitForFoundryModelStatusAsync("playwright-session", "Cached", TimeSpan.FromSeconds(30)),
                TimeSpan.FromSeconds(45));
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
            throw new TimeoutException($"Playwright LLM setup stage '{stage}' exceeded {timeout}.", exception);
        }
    }
}