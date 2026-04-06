using LiCvWriter.Core.Documents;

namespace LiCvWriter.Application.Models;

public sealed record DocumentExportResult(
    DocumentKind Kind,
    string MarkdownPath);
