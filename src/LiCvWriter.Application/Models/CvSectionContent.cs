using LiCvWriter.Core.Documents;

namespace LiCvWriter.Application.Models;

/// <summary>
/// Generated markdown content for a single <see cref="CvSection"/>, along with
/// the LLM metrics captured while producing it. Metrics may be null when the
/// content was assembled deterministically (no LLM call was made).
/// </summary>
public sealed record CvSectionContent(
    CvSection Section,
    string Markdown,
    TimeSpan? LlmDuration = null,
    long? PromptTokens = null,
    long? CompletionTokens = null);
