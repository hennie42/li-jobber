using Microsoft.Playwright;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class LinkedInSetupE2ETests(PlaywrightAppFixture fixture) : IClassFixture<PlaywrightAppFixture>
{
    [LivePlaywrightFact]
    public async Task SnapshotDomains_WhenSelectionChanges_PersistsAcrossReload()
    {
        var context = await fixture.CreateContextAsync(recordVideo: false);
        IPage? page = null;

        try
        {
            await RunWithTimeoutAsync("create browser page", async () => page = await context.NewPageAsync(), TimeSpan.FromSeconds(30));
            if (page is null)
            {
                throw new InvalidOperationException("Playwright did not create a browser page for the LinkedIn setup E2E test.");
            }

            var linkedInSetupPage = new LinkedInSetupPage(page, fixture.BaseUrl);

            await RunWithTimeoutAsync("open linkedin setup", linkedInSetupPage.GotoAsync, TimeSpan.FromMinutes(2));
            await RunWithTimeoutAsync("verify default domain count", () => linkedInSetupPage.WaitForSelectedCountAsync(14), TimeSpan.FromSeconds(30));
            await RunWithTimeoutAsync("disable projects", () => linkedInSetupPage.SetDomainSelectedAsync("Projects", false), TimeSpan.FromSeconds(30));
            await RunWithTimeoutAsync("verify projects removal applied", () => linkedInSetupPage.WaitForSelectedCountAsync(13), TimeSpan.FromSeconds(30));
            await RunWithTimeoutAsync("enable endorsements", () => linkedInSetupPage.SetDomainSelectedAsync("Endorsements", true), TimeSpan.FromSeconds(30));
            await RunWithTimeoutAsync("verify updated domain count", () => linkedInSetupPage.WaitForSelectedCountAsync(14), TimeSpan.FromSeconds(30));
            await RunWithTimeoutAsync("reload linkedin setup", linkedInSetupPage.ReloadAsync, TimeSpan.FromMinutes(1));
            await RunWithTimeoutAsync("assert projects stayed disabled", () => linkedInSetupPage.AssertDomainSelectedAsync("Projects", false), TimeSpan.FromSeconds(30));
            await RunWithTimeoutAsync("assert endorsements stayed enabled", () => linkedInSetupPage.AssertDomainSelectedAsync("Endorsements", true), TimeSpan.FromSeconds(30));
            await RunWithTimeoutAsync("verify persisted domain count", () => linkedInSetupPage.WaitForSelectedCountAsync(14), TimeSpan.FromSeconds(30));
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
            throw new TimeoutException($"Playwright LinkedIn setup stage '{stage}' exceeded {timeout}.", exception);
        }
    }
}