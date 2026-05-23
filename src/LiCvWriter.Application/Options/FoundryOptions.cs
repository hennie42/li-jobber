namespace LiCvWriter.Application.Options;

/// <summary>
/// Configures the Microsoft Foundry Local runtime used for in-process model discovery,
/// download, and chat completions.
/// </summary>
public sealed class FoundryOptions
{
    public const string SectionName = "Foundry";

    public string AppName { get; set; } = "LI-CV-Writer";

    public string AppDataDir { get; set; } = string.Empty;

    public string ModelCacheDir { get; set; } = string.Empty;

    public string LogsDir { get; set; } = string.Empty;

    public string DefaultModelAlias { get; set; } = "phi-3.5-mini";

    public bool AllowDownloads { get; set; } = true;

    public bool AutoLoadSelectedModel { get; set; } = true;

    public bool UseWindowsMlAcceleration { get; set; } = true;

    public bool AutoRegisterExecutionProviders { get; set; }

    public string[] PreferredExecutionProviders { get; set; } = [];
}