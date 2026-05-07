using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace LiCvWriter.Tests.Web.E2E;

public sealed class JobWorkbenchBatchFlowE2ETests(PlaywrightAppFixture fixture) : IClassFixture<PlaywrightAppFixture>
{
    [LivePlaywrightFact]
    public async Task StartSelected_WithThreeReadyJobSets_ShowsLiveLlmActivityAndWorkbenchProgress()
    {
        var seed = await fixture.SeedDemoAsync();
        var artifacts = new DemoArtifactWriter(fixture.RepositoryRoot);
        var context = await fixture.CreateContextAsync(recordVideo: artifacts.Enabled);
        await CompanyNameMasker.InstallAsync(context, seed.CompanyNames);
        await artifacts.StartTraceAsync(context);
        IPage? page = null;
        var flowCompleted = false;

        try
        {
            await RunWithTimeoutAsync("create browser page", async () => page = await context.NewPageAsync(), TimeSpan.FromSeconds(60));
            if (page is null)
            {
                throw new InvalidOperationException("Playwright did not create a browser page for the Job Workbench E2E test.");
            }

            var workbench = new JobWorkbenchPage(page, fixture.BaseUrl);
            if (artifacts.Enabled)
            {
                var walkthrough = new GuidedDemoWalkthrough(page, workbench, artifacts, seed.CompanyNames);
                await RunWithTimeoutAsync("record guided demo", walkthrough.RecordAsync, TimeSpan.FromMinutes(11));
            }
            else
            {
                await RunWithTimeoutAsync(
                    "run live workbench batch flow",
                    async () =>
                    {
                        await workbench.GotoAsync();
                        await CompanyNameMasker.StartAsync(page, seed.CompanyNames);
                        await Expect(workbench.SelectedJobCheckboxes).ToHaveCountAsync(0);
                        await workbench.SelectFirstJobSetsAsync(3);
                        await workbench.StartSelectedAsync();
                        await workbench.WaitForLiveLlmProgressAsync();
                    },
                    TimeSpan.FromMinutes(4));
            }

            flowCompleted = true;
        }
        finally
        {
            if (page is not null)
            {
                await artifacts.CompleteAsync(page, context, validateArtifacts: flowCompleted);
            }
            else
            {
                await context.CloseAsync();
            }
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
            throw new TimeoutException($"Playwright E2E stage '{stage}' exceeded {timeout}.", exception);
        }
    }
}
