namespace LiCvWriter.Application.Models;

/// <summary>
/// Snapshot of an end-to-end run that benchmarks selected local models for a
/// specific provider. The same record is used both for the live progress view
/// and the final ranked results, so the UI can render either state from a
/// single source of truth.
/// </summary>
public sealed record ModelBenchmarkSession(
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    bool IsRunning,
    bool IsCancelled,
    int CompletedCount,
    int TotalCount,
    string? CurrentModel,
    IReadOnlyList<ModelBenchmarkResult> Results,
    LlmProviderKind Provider = LlmProviderKind.Ollama)
{
    /// <summary>
    /// Gets the current typed benchmark phase so the UI does not need to infer state from log text.
    /// </summary>
    public ModelBenchmarkRunPhase CurrentPhase { get; init; } = ModelBenchmarkRunPhase.Pending;

    /// <summary>
    /// Gets the current progress detail for the active model benchmark run.
    /// </summary>
    public string? CurrentDetail { get; init; }

    /// <summary>
    /// Gets the current benchmark fixture identifier when a fixture is in flight.
    /// </summary>
    public string? CurrentFixtureId { get; init; }

    /// <summary>
    /// Gets the human-readable name of the active benchmark fixture.
    /// </summary>
    public string? CurrentFixtureDisplayName { get; init; }

    /// <summary>
    /// Gets the prompt identifier associated with the active benchmark fixture.
    /// </summary>
    public string? CurrentPromptId { get; init; }

    /// <summary>
    /// Gets the 1-based active fixture number for the current model when a fixture is running.
    /// </summary>
    public int CurrentFixtureNumber { get; init; }

    /// <summary>
    /// Gets how many fixtures are already completed for the current model.
    /// </summary>
    public int CompletedFixtureCount { get; init; }

    /// <summary>
    /// Gets how many fixtures are configured for the current model benchmark.
    /// </summary>
    public int TotalFixtureCount { get; init; }

    /// <summary>
    /// Gets when the live benchmark snapshot itself was most recently updated.
    /// </summary>
    public DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>
    /// Gets when the benchmark last observed real worker-side progress.
    /// </summary>
    public DateTimeOffset? LastRealProgressUtc { get; init; }

    /// <summary>
    /// Gets the current hang-monitoring state for the active model.
    /// </summary>
    public ModelBenchmarkHangState HangState { get; init; } = ModelBenchmarkHangState.None;

    /// <summary>
    /// Gets the current hang-monitoring detail shown to the live UI.
    /// </summary>
    public string? HangDetail { get; init; }

    /// <summary>
    /// Gets when the current hang warning was first raised.
    /// </summary>
    public DateTimeOffset? HangWarningStartedUtc { get; init; }

    /// <summary>
    /// Gets when the current hang warning will escalate to a model failure if progress does not resume.
    /// </summary>
    public DateTimeOffset? HangDeadlineUtc { get; init; }

    /// <summary>
    /// Gets the typed diagnostics collected for the active or most recently completed model.
    /// </summary>
    public ModelBenchmarkDiagnostics Diagnostics { get; init; } = ModelBenchmarkDiagnostics.Empty;

    /// <summary>
    /// Gets when the current benchmark phase started for the active model.
    /// </summary>
    public DateTimeOffset? CurrentPhaseStartedUtc { get; init; }
}
