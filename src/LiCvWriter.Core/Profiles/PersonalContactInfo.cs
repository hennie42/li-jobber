namespace LiCvWriter.Core.Profiles;

/// <summary>
/// Personal contact details supplied at draft generation time.
/// The current workspace reuses the same values across jobsets and persists
/// them locally so the user does not need to re-enter them for each tab.
/// All fields are optional; the renderer omits any line that is null/blank.
/// </summary>
public sealed record PersonalContactInfo(
    string? Email = null,
    string? Phone = null,
    string? LinkedInUrl = null,
    string? City = null)
{
    public static PersonalContactInfo Empty { get; } = new();

    public bool HasAnyValue =>
        !string.IsNullOrWhiteSpace(Email)
        || !string.IsNullOrWhiteSpace(Phone)
        || !string.IsNullOrWhiteSpace(LinkedInUrl)
        || !string.IsNullOrWhiteSpace(City);
}
