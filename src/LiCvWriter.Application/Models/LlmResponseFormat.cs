namespace LiCvWriter.Application.Models;

/// <summary>
/// How the LLM should structure its response. Maps directly to the Ollama
/// <c>format</c> field on the chat/generate endpoints.
/// </summary>
public sealed record LlmResponseFormat
{
    /// <summary>The model is asked to emit valid JSON, with no schema constraint.</summary>
    public static LlmResponseFormat Json { get; } = new("json", null);

    /// <summary>The model is asked to emit JSON that matches the supplied JSON Schema.</summary>
    public static LlmResponseFormat Schema(string schemaJson) => new(null, schemaJson);

    private LlmResponseFormat(string? format, string? schema)
    {
        Format = format;
        SchemaJson = schema;
    }

    public string? Format { get; }

    public string? SchemaJson { get; }
}
