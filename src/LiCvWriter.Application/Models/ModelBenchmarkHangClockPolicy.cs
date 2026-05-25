namespace LiCvWriter.Application.Models;

/// <summary>
/// Defines benchmark-only inactivity monitoring thresholds for the model queue.
/// </summary>
public sealed record ModelBenchmarkHangClockPolicy(
    TimeSpan WarningAfter,
    TimeSpan GracePeriod,
    TimeSpan PollInterval,
    TimeSpan? PreparationWarningAfter = null,
    TimeSpan? PreparationGracePeriod = null)
{
    /// <summary>
    /// Conservative default that warns on prolonged silence and gives a grace window before failing the current model.
    /// </summary>
    public static ModelBenchmarkHangClockPolicy Default { get; } = new(
        WarningAfter: TimeSpan.FromMinutes(2),
        GracePeriod: TimeSpan.FromMinutes(1),
        PollInterval: TimeSpan.FromSeconds(5),
        PreparationWarningAfter: TimeSpan.FromMinutes(4),
        PreparationGracePeriod: TimeSpan.FromMinutes(2));

    /// <summary>
    /// Returns the inactivity threshold for the active benchmark phase.
    /// </summary>
    public TimeSpan GetWarningAfter(ModelBenchmarkRunPhase phase)
        => phase == ModelBenchmarkRunPhase.Preparing
            ? PreparationWarningAfter ?? WarningAfter
            : WarningAfter;

    /// <summary>
    /// Returns the post-warning grace period for the active benchmark phase.
    /// </summary>
    public TimeSpan GetGracePeriod(ModelBenchmarkRunPhase phase)
        => phase == ModelBenchmarkRunPhase.Preparing
            ? PreparationGracePeriod ?? GracePeriod
            : GracePeriod;
}