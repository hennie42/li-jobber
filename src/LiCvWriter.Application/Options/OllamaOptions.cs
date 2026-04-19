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
    /// Minimum character count before repetition-loop detection kicks in during streaming.
    /// Set to 0 or negative to disable. Default is 500.
    /// </summary>
    public int RepetitionDetectionMinLength { get; set; } = 500;
}
