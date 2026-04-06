namespace LiCvWriter.Core.Profiles;

public sealed record EducationEntry(
    string SchoolName,
    string? DegreeName,
    string? Notes,
    string? Activities,
    DateRange Period);
