using System.Text;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Auditing;

namespace LiCvWriter.Infrastructure.Storage;

public sealed class LocalMarkdownAuditStore(StorageOptions options) : IAuditStore
{
    public async Task SaveAsync(AuditTrailEntry entry, CancellationToken cancellationToken = default)
    {
        var root = ExpandPath(options.AuditRoot);
        Directory.CreateDirectory(root);

        var fileName = $"{entry.CreatedAtUtc:yyyyMMdd-HHmmss}-{SanitizeFileName(entry.EventType)}.md";
        var path = Path.Combine(root, fileName);

        var builder = new StringBuilder();
        builder.AppendLine($"# {entry.EventType}");
        builder.AppendLine();
        builder.AppendLine($"- Created: {entry.CreatedAtUtc:O}");
        builder.AppendLine();
        builder.AppendLine(entry.Summary);
        builder.AppendLine();
        builder.AppendLine("## Metadata");
        builder.AppendLine();

        foreach (var pair in entry.Metadata.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {pair.Key}: {pair.Value}");
        }

        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken);
    }

    private static string ExpandPath(string path)
        => Environment.ExpandEnvironmentVariables(path.Replace('/', Path.DirectorySeparatorChar));

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalidChars.Contains(character) ? '-' : character));
    }
}