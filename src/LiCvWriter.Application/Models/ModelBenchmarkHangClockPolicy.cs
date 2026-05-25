namespace LiCvWriter.Application.Models;

/// <summary>
/// Defines benchmark-only inactivity monitoring thresholds for the model queue.
/// </summary>
public sealed record ModelBenchmarkHangClockPolicy(TimeSpan WarningAfter, TimeSpan GracePeriod, TimeSpan PollInterval)
{
    /// <summary>
    /// Conservative default that warns on prolonged silence and gives a grace window before failing the current model.
    /// </summary>
    public static ModelBenchmarkHangClockPolicy Default { get; } = new(
        WarningAfter: TimeSpan.FromMinutes(2),
        GracePeriod: TimeSpan.FromMinutes(1),
        PollInterval: TimeSpan.FromSeconds(5));
}