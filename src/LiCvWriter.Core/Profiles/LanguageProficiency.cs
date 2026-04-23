namespace LiCvWriter.Core.Profiles;

/// <summary>
/// A single spoken/written language entry on the CV, parsed from the
/// LinkedIn export's Languages signal (or supplied manually). The
/// <paramref name="Level"/> is a free-text proficiency label (e.g.
/// "Native", "Professional working", "Modersmål", "Flydende") so it
/// round-trips both English and Danish source data without normalisation.
/// </summary>
public sealed record LanguageProficiency(
    string Language,
    string? Level = null);
