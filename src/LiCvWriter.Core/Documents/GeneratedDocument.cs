namespace LiCvWriter.Core.Documents;

public sealed record GeneratedDocument(
    DocumentKind Kind,
    string Title,
    string Markdown,
    string? PlainText,
    DateTimeOffset GeneratedAtUtc,
    string? OutputPath = null,
    TimeSpan? LlmDuration = null,
    long? PromptTokens = null,
    long? CompletionTokens = null,
    string? Model = null);
