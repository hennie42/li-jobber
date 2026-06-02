namespace LiCvWriter.Tests.Web.E2E;

public sealed class BenchmarkDemoFactAttribute : FactAttribute
{
    public BenchmarkDemoFactAttribute()
    {
        Timeout = 1_200_000;

        if (!LivePlaywrightFactAttribute.IsEnabled)
        {
            Skip = "Set LICVWRITER_RUN_PLAYWRIGHT_E2E=1 to run live Playwright tests.";
            return;
        }

        if (!IsEnabled)
        {
            Skip = "Set LICVWRITER_PLAYWRIGHT_WRITE_BENCHMARK_DEMO=1 to record the benchmark Playwright demo.";
        }
    }

    public static bool IsEnabled
        => string.Equals(Environment.GetEnvironmentVariable("LICVWRITER_PLAYWRIGHT_WRITE_BENCHMARK_DEMO"), "1", StringComparison.OrdinalIgnoreCase);
}