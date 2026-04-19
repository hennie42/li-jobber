using LiCvWriter.Core.Documents;

namespace LiCvWriter.Application.Models;

/// <summary>
/// Result of exporting a generated document to disk. The file is always a
/// Word document (<c>.docx</c>) — Markdown is no longer written to disk.
/// <para>
/// <see cref="FilePath"/> may be null or empty when deserializing snapshots
/// saved before the Phase 1 export refactor; callers should guard accordingly.
/// </para>
/// </summary>
public sealed record DocumentExportResult(
    DocumentKind Kind,
    string? FilePath);
