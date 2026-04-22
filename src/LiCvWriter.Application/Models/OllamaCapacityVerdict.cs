namespace LiCvWriter.Application.Models;

/// <summary>
/// Coarse-grained classification of how comfortably the chosen model fits
/// the host hardware. Combined static (file size vs reported VRAM) and
/// dynamic (decode tokens/sec from a warm-up call) signals.
/// </summary>
public enum OllamaCapacityFit
{
    Unknown = 0,
    Comfortable,
    Usable,
    PartialOffload,
    CpuOnly,
    TooLargeForInteractive
}

/// <summary>
/// Result of a single capacity probe against an Ollama model.
/// </summary>
public sealed record OllamaCapacityVerdict(
    string Model,
    OllamaCapacityFit Fit,
    string Headline,
    IReadOnlyList<string> Notes,
    double? DecodeTokensPerSecond,
    double? PromptTokensPerSecond,
    TimeSpan? LoadDuration,
    long? VramBytes,
    long? ResidentSizeBytes,
    double? GpuOffloadRatio,
    OllamaModelInfo? ModelInfo,
    DateTimeOffset MeasuredAtUtc)
{
    public static OllamaCapacityVerdict Unknown(string model, string reason)
        => new(model, OllamaCapacityFit.Unknown, reason, Array.Empty<string>(),
            null, null, null, null, null, null, null, DateTimeOffset.UtcNow);
}
