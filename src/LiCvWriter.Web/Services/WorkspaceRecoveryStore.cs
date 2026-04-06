using System.Text.Json;
using LiCvWriter.Application.Options;

namespace LiCvWriter.Web.Services;

public sealed class WorkspaceRecoveryStore(StorageOptions storageOptions)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly string filePath = Path.Combine(
        ExpandPath(storageOptions.WorkingRoot),
        "workspace-recovery.json");

    public WorkspaceRecoverySnapshot? Load()
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return null;
                }

                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<WorkspaceRecoverySnapshot>(json, SerializerOptions);
            }
            catch
            {
                return null;
            }
        }
    }

    public void Save(WorkspaceRecoverySnapshot snapshot)
    {
        lock (gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
                File.WriteAllText(filePath, json);
            }
            catch
            {
                // Recovery metadata should never block the interactive app flow.
            }
        }
    }

    private static string ExpandPath(string path)
        => Environment.ExpandEnvironmentVariables(path.Replace('/', Path.DirectorySeparatorChar));
}