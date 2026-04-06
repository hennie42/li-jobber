namespace LiCvWriter.Core.Profiles;

public sealed record ProjectEntry(
    string Title,
    string? Description,
    Uri? Url,
    DateRange Period);
