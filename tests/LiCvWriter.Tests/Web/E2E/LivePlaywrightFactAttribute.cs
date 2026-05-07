namespace LiCvWriter.Tests.Web.E2E;

public sealed class LivePlaywrightFactAttribute : FactAttribute
{
    public LivePlaywrightFactAttribute()
    {
        Timeout = 900_000;

        if (!IsEnabled)
        {
            Skip = "Set LICVWRITER_RUN_PLAYWRIGHT_E2E=1 to run live Playwright tests.";
        }
    }

    public static bool IsEnabled
        => string.Equals(Environment.GetEnvironmentVariable("LICVWRITER_RUN_PLAYWRIGHT_E2E"), "1", StringComparison.OrdinalIgnoreCase);
}
