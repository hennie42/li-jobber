namespace LiCvWriter.Application.Options;

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    public string BaseUrl { get; set; } = "http://localhost:11434/api";

    public string Model { get; set; } = "nemotron-cascade-2:latest";

    public int StatusTimeoutSeconds { get; set; } = 15;

    public bool UseChatEndpoint { get; set; } = true;

    public string KeepAlive { get; set; } = "10m";

    public int MaxOperationSeconds { get; set; } = 480;

    public string Think { get; set; } = "low";

    public double Temperature { get; set; } = 0.2;
}
