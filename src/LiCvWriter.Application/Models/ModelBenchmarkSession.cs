namespace LiCvWriter.Application.Models;

/// <summary>
/// Snapshot of an end-to-end run that benchmarks every installed Ollama model.
/// The same record is used both for the live progress view and the final ranked
/// results, so the UI can render either state from a single source of truth.
/// </summary>
public sealed record ModelBenchmarkSession(
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    bool IsRunning,
    bool IsCancelled,
    int CompletedCount,
    int TotalCount,
    string? CurrentModel,
    IReadOnlyList<ModelBenchmarkResult> Results);
