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
    string? Model = null,
    IReadOnlyList<CvSectionMarkdown>? GeneratedSections = null);

/// <summary>
/// Per-section CV markdown produced by a dedicated LLM call. Mirrors
/// <c>CvSectionContent</c> from the application layer but lives in Core so
/// downstream services (export, validation) can attach it to a document
/// without introducing an Application reference.
/// </summary>
public sealed record CvSectionMarkdown(CvSection Section, string Markdown);
