namespace LiCvWriter.Web.Services;

/// <summary>
/// Normalizes raw progress values reported by Foundry Local before they are rendered in the UI.
/// </summary>
public static class FoundryProgressFormatter
{
    /// <summary>
    /// Formats a user-facing progress detail string for Foundry downloads and provider registration.
    /// </summary>
    public static string FormatDetail(string prefix, double rawProgress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var normalizedPercent = NormalizePercent(rawProgress);
        return normalizedPercent is { } percent
            ? $"{prefix}: {percent:0.0}%"
            : $"{prefix}. Progress reported by Foundry Local.";
    }

    /// <summary>
    /// Converts the raw Foundry Local progress payload into a stable percentage when the unit is recognizable.
    /// </summary>
    public static double? NormalizePercent(double rawProgress)
    {
        if (double.IsNaN(rawProgress) || double.IsInfinity(rawProgress) || rawProgress < 0.0)
        {
            return null;
        }

        var percent = rawProgress switch
        {
            <= 1.0 => rawProgress * 100.0,
            <= 100.0 => rawProgress,
            <= 10_000.0 => rawProgress / 100.0,
            _ => double.NaN
        };

        return double.IsNaN(percent)
            ? null
            : Math.Clamp(percent, 0.0, 100.0);
    }
}