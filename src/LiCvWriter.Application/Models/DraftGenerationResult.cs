using LiCvWriter.Core.Documents;

namespace LiCvWriter.Application.Models;

public sealed record DraftGenerationResult(
    IReadOnlyList<GeneratedDocument> Documents,
    IReadOnlyList<DocumentExportResult> Exports);
