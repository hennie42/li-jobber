namespace LiCvWriter.Application.Services;

/// <summary>
/// Classifies whether a Foundry catalog model is a good fit for the current
/// text and JSON benchmark fixtures.
/// </summary>
public static class FoundryTextBenchmarkSuitabilityEvaluator
{
    private static readonly string[] AudioMarkers =
    [
        "audio",
        "asr",
        "speech",
        "speech to text",
        "speech-to-text",
        "transcribe",
        "transcription",
        "whisper"
    ];

    private static readonly string[] EmbeddingMarkers =
    [
        "embed",
        "embedding",
        "embeddings"
    ];

    private static readonly string[] RerankMarkers =
    [
        "rerank",
        "reranker",
        "ranking model"
    ];

    private static readonly string[] VisionMarkers =
    [
        "image",
        "vision",
        "visual"
    ];

    private static readonly string[] Phi4Markers =
    [
        "phi-4",
        "phi 4"
    ];

    public static (bool IsUsable, string? Reason) Evaluate(
        string alias,
        string displayName,
        string? description,
        IEnumerable<string?>? metadata = null)
    {
        var descriptors = BuildDescriptorText(alias, displayName, description, metadata);

        if (ContainsAny(descriptors, AudioMarkers))
        {
            return (false, "Audio transcription model; text JSON benchmark is not a good fit.");
        }

        if (ContainsAny(descriptors, EmbeddingMarkers))
        {
            return (false, "Embedding model; text JSON benchmark is not a good fit.");
        }

        if (ContainsAny(descriptors, RerankMarkers))
        {
            return (false, "Reranking model; text JSON benchmark is not a good fit.");
        }

        if (ContainsAny(descriptors, VisionMarkers) && !Contains(descriptors, "instruct"))
        {
            return (false, "Vision-focused model; text JSON benchmark is not a good fit.");
        }

        if (ContainsAny(descriptors, Phi4Markers))
        {
            return (false, "Phi-4 model; structured text extraction can repeat instead of reliably returning clean JSON.");
        }

        return (true, null);
    }

    private static string BuildDescriptorText(
        string alias,
        string displayName,
        string? description,
        IEnumerable<string?>? metadata)
    {
        var parts = new List<string>(3)
        {
            alias,
            displayName
        };

        if (!string.IsNullOrWhiteSpace(description))
        {
            parts.Add(description);
        }

        if (metadata is not null)
        {
            parts.AddRange(metadata.Where(static value => !string.IsNullOrWhiteSpace(value))!);
        }

        return string.Join(" ", parts);
    }

    private static bool ContainsAny(string value, IEnumerable<string> markers)
        => markers.Any(marker => Contains(value, marker));

    private static bool Contains(string value, string marker)
        => value.Contains(marker, StringComparison.OrdinalIgnoreCase);
}