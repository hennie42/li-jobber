using LiCvWriter.Application.Models;
using LiCvWriter.Core.Documents;

namespace LiCvWriter.Application.Abstractions;

public interface IDocumentExportService
{
    Task<DocumentExportResult> ExportAsync(GeneratedDocument document, CancellationToken cancellationToken = default);
}
