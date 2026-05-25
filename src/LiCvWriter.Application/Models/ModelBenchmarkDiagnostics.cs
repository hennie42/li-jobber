namespace LiCvWriter.Application.Models;

/// <summary>
/// Captures typed timing and runtime-path signals for a single benchmarked model.
/// These diagnostics are additive to the user-facing notes so the UI and tests can
/// reason about prepare, warm-up, evaluation, and cleanup behavior without parsing free text.
/// </summary>
public sealed record ModelBenchmarkDiagnostics
{
    public static ModelBenchmarkDiagnostics Empty { get; } = new();

    public TimeSpan? PreparationDuration { get; init; }

    public TimeSpan? WarmupDuration { get; init; }

    public TimeSpan? EvaluationDuration { get; init; }

    public TimeSpan? CleanupDuration { get; init; }

    public TimeSpan? FinalizationDuration { get; init; }

    public string? AccelerationReadiness { get; init; }

    public string? AccelerationStatusMessage { get; init; }

    public string? RuntimePathSummary { get; init; }
}