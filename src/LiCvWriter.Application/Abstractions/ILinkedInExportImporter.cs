using LiCvWriter.Application.Models;

namespace LiCvWriter.Application.Abstractions;

public interface ILinkedInExportImporter
{
    Task<LinkedInExportImportResult> ImportAsync(string exportRootPath, CancellationToken cancellationToken = default);

    Task<LinkedInExportImportResult> ImportMemberSnapshotAsync(
        string accessToken,
        Action<string>? onProgress = null,
        IReadOnlyCollection<string>? selectedDomains = null,
        CancellationToken cancellationToken = default);
}
