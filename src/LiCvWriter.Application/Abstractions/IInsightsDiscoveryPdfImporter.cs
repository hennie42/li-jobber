using LiCvWriter.Application.Models;

namespace LiCvWriter.Application.Abstractions;

public interface IInsightsDiscoveryPdfImporter
{
    Task<InsightsDiscoveryPdfImportResult> ImportAsync(Stream pdfStream, CancellationToken cancellationToken = default);
}