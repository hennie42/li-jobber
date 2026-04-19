using LiCvWriter.Core.Documents;

namespace LiCvWriter.Application.Models;

/// <summary>
/// Result of exporting a generated document to disk. The file is always a
/// Word document (<c>.docx</c>) — Markdown is no longer written to disk.
/// </summary>
public sealed record DocumentExportResult(
    DocumentKind Kind,
    string FilePath);
