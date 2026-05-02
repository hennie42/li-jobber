namespace LiCvWriter.Application.Options;

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    public string BaseUrl { get; set; } = "http://localhost:11434/api";

    public string Model { get; set; } = "nemotron-cascade-2:latest";

    public int StatusTimeoutSeconds { get; set; } = 15;

    public bool UseChatEndpoint { get; set; } = true;

    public string KeepAlive { get; set; } = "10m";

    public int MaxOperationSeconds { get; set; } = 0;

    public int StreamingInactivitySeconds { get; set; } = 90;

    public string Think { get; set; } = "low";

    public double Temperature { get; set; } = 0.2;

    /// <summary>
    /// Minimum character count before repetition-loop detection kicks in for <em>response content</em> during streaming.
    /// Set to 0 or negative to disable. Default is 500.
    /// </summary>
    public int RepetitionDetectionMinLength { get; set; } = 500;

    /// <summary>
    /// Minimum character count before repetition-loop detection kicks in for <em>thinking content</em> during streaming.
    /// Set to 0 or negative to disable (default). Large reasoning models legitimately produce tens of thousands
    /// of thinking characters before producing output; the inactivity timeout acts as the backstop for genuine stalls.
    /// </summary>
    public int RepetitionDetectionThinkingMinLength { get; set; } = 0;

    /// <summary>
    /// Optional default Ollama context window (num_ctx). Set to a positive value to override
    /// the model's built-in default; leave 0 to fall back to the model's default. Long
    /// prompts (e.g. CV draft generation with full evidence) benefit from 8192 or higher.
    /// </summary>
    public int NumCtx { get; set; } = 0;

    /// <summary>
    /// num_predict cap used by the capacity warm-up call. Kept small so the probe is cheap.
    /// </summary>
    public int CapacityWarmupNumPredict { get; set; } = 64;

    /// <summary>
    /// Decode tok/s threshold below which a model is flagged as too large for interactive use.
    /// </summary>
    public double CapacityTooSlowTokensPerSecond { get; set; } = 8.0;

    /// <summary>
    /// Decode tok/s threshold at or above which a model is flagged as comfortable.
    /// </summary>
    public double CapacityComfortableTokensPerSecond { get; set; } = 25.0;

    /// <summary>
    /// Whether to run a capacity probe automatically when the user selects a session model.
    /// </summary>
    public bool RunCapacityProbeOnModelSelect { get; set; } = true;

    /// <summary>
    /// Number of attempts for a "Refresh all analysis" operation before giving up.
    /// Set to 1 to disable retry. Default is 3.
    /// </summary>
    public int RetryAttempts { get; set; } = 3;

    /// <summary>
    /// Seconds to wait between retry attempts for "Refresh all analysis".
    /// Default is 30.
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 30;
}
