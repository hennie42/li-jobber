namespace LiCvWriter.Core.Profiles;

public sealed record CertificationEntry(
    string Name,
    string? Authority,
    Uri? Url,
    DateRange Period,
    string? LicenseNumber);
