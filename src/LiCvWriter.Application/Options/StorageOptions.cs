namespace LiCvWriter.Application.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string WorkingRoot { get; set; } = "%LOCALAPPDATA%/LiCvWriter";

    public string AuditRoot { get; set; } = "%LOCALAPPDATA%/LiCvWriter/Audit";

    public string ExportRoot { get; set; } = "%LOCALAPPDATA%/LiCvWriter/Exports";
}
