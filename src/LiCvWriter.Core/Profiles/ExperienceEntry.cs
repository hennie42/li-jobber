namespace LiCvWriter.Core.Profiles;

public sealed record ExperienceEntry(
    string CompanyName,
    string Title,
    string? Description,
    string? Location,
    DateRange Period,
    IReadOnlyList<string>? Highlights = null);
