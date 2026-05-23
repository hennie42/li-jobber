namespace LiCvWriter.Application.Models;

/// <summary>
/// High-level phases for a single model benchmark run.
/// </summary>
public enum ModelBenchmarkRunPhase
{
    Pending,
    Preparing,
    Warmup,
    Evaluating,
    Cleanup,
    Finalizing,
    Completed,
    Cancelled
}