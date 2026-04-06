using System.Text;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Documents;

namespace LiCvWriter.Infrastructure.Documents;

public sealed class LocalDocumentExportService(StorageOptions options) : IDocumentExportService
{
    public async Task<DocumentExportResult> ExportAsync(GeneratedDocument document, CancellationToken cancellationToken = default)
    {
        var exportRoot = ExpandPath(options.ExportRoot);
        var exportFolder = ResolveExportFolder(exportRoot, document.OutputPath);
        Directory.CreateDirectory(exportFolder);

        var timestamp = document.GeneratedAtUtc.ToString("yyyyMMdd-HHmmss");
        var safeFileStem = SanitizeFileName($"{timestamp}-{document.Kind}-{document.Title}");

        var markdownPath = Path.Combine(exportFolder, $"{safeFileStem}.md");

        await File.WriteAllTextAsync(markdownPath, document.Markdown, Encoding.UTF8, cancellationToken);

        return new DocumentExportResult(document.Kind, markdownPath);
    }

    private static string ExpandPath(string path)
        => Environment.ExpandEnvironmentVariables(path.Replace('/', Path.DirectorySeparatorChar));

    private static string ResolveExportFolder(string exportRoot, string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return exportRoot;
        }

        var normalized = outputPath.Trim();
        return Path.IsPathRooted(normalized)
            ? normalized
            : Path.Combine(exportRoot, normalized);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '-' : character));
    }
}