namespace LiCvWriter.Tests.Web;

public sealed class LiveWindowsFoundryFactAttribute : FactAttribute
{
    public LiveWindowsFoundryFactAttribute()
    {
        Timeout = 900_000;

        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only Foundry smoke tests can only run on Windows.";
            return;
        }

        if (!IsEnabled)
        {
            Skip = "Set LICVWRITER_RUN_WINDOWS_FOUNDRY_SMOKE=1 to run live Windows Foundry smoke tests.";
        }
    }

    public static bool IsEnabled
        => string.Equals(Environment.GetEnvironmentVariable("LICVWRITER_RUN_WINDOWS_FOUNDRY_SMOKE"), "1", StringComparison.OrdinalIgnoreCase);
}