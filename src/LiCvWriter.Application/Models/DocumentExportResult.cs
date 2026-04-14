using LiCvWriter.Core.Documents;

namespace LiCvWriter.Application.Models;

/// <summary>
/// Result of exporting a generated document to disk.
/// </summary>
public sealed record DocumentExportResult(
    DocumentKind Kind,
    string MarkdownPath,
    string? WordPath = null);
