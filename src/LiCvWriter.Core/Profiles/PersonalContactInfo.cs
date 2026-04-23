namespace LiCvWriter.Core.Profiles;

/// <summary>
/// Per-job personal contact details supplied at draft generation time.
/// Intentionally not persisted: the user enters these on the draft screen
/// for each export so different roles can use different contact channels
/// (e.g. a professional email for one application, a personal one for another).
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
