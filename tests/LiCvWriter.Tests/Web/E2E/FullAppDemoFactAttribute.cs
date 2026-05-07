namespace LiCvWriter.Tests.Web.E2E;

public sealed class FullAppDemoFactAttribute : FactAttribute
{
    public FullAppDemoFactAttribute()
    {
        Timeout = 900_000;

        if (!LivePlaywrightFactAttribute.IsEnabled)
        {
            Skip = "Set LICVWRITER_RUN_PLAYWRIGHT_E2E=1 to run live Playwright tests.";
            return;
        }

        if (!IsEnabled)
        {
            Skip = "Set LICVWRITER_PLAYWRIGHT_WRITE_FULL_DEMO=1 to record the full-app Playwright demo.";
        }
    }

    public static bool IsEnabled
        => string.Equals(Environment.GetEnvironmentVariable("LICVWRITER_PLAYWRIGHT_WRITE_FULL_DEMO"), "1", StringComparison.OrdinalIgnoreCase);
}