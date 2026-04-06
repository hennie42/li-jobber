using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Application.Services;

public sealed class CandidateProfileMergeService
{
    public CandidateProfile Merge(
        CandidateProfile csvProfile,
        CandidateProfile? liveProfile = null,
        IReadOnlyDictionary<string, string>? manualSignals = null)
    {
        var current = liveProfile ?? CandidateProfile.Empty;

        return csvProfile with
        {
            Headline = Prefer(current.Headline, csvProfile.Headline),
            Summary = Prefer(current.Summary, csvProfile.Summary),
            Industry = Prefer(current.Industry, csvProfile.Industry),
            Location = Prefer(current.Location, csvProfile.Location),
            PrimaryEmail = Prefer(current.PrimaryEmail, csvProfile.PrimaryEmail),
            PublicProfileUrl = Prefer(current.PublicProfileUrl, csvProfile.PublicProfileUrl),
            Experience = MergeOrdered(csvProfile.Experience, current.Experience, static item => $"{item.CompanyName}|{item.Title}|{item.Period.DisplayValue}"),
            Education = MergeOrdered(csvProfile.Education, current.Education, static item => $"{item.SchoolName}|{item.DegreeName}|{item.Period.DisplayValue}"),
            Skills = MergeOrdered(csvProfile.Skills, current.Skills, static item => item.Name),
            Certifications = MergeOrdered(csvProfile.Certifications, current.Certifications, static item => item.Name),
            Projects = MergeOrdered(csvProfile.Projects, current.Projects, static item => item.Title),
            Recommendations = MergeOrdered(csvProfile.Recommendations, current.Recommendations, static item => $"{item.Author.FullName}|{item.Company}|{item.CreatedOn}"),
            ManualSignals = MergeSignals(csvProfile.ManualSignals, manualSignals)
        };
    }

    public CandidateProfile MergePreferPrimary(
        CandidateProfile primaryProfile,
        CandidateProfile? fallbackProfile = null,
        IReadOnlyDictionary<string, string>? manualSignals = null)
    {
        var fallback = fallbackProfile ?? CandidateProfile.Empty;

        return primaryProfile with
        {
            Name = primaryProfile.Name == PersonName.Empty ? fallback.Name : primaryProfile.Name,
            Headline = Prefer(primaryProfile.Headline, fallback.Headline),
            Summary = Prefer(primaryProfile.Summary, fallback.Summary),
            Industry = Prefer(primaryProfile.Industry, fallback.Industry),
            Location = Prefer(primaryProfile.Location, fallback.Location),
            PrimaryEmail = Prefer(primaryProfile.PrimaryEmail, fallback.PrimaryEmail),
            PublicProfileUrl = Prefer(primaryProfile.PublicProfileUrl, fallback.PublicProfileUrl),
            Experience = MergeOrdered(primaryProfile.Experience, fallback.Experience, static item => $"{item.CompanyName}|{item.Title}|{item.Period.DisplayValue}"),
            Education = MergeOrdered(primaryProfile.Education, fallback.Education, static item => $"{item.SchoolName}|{item.DegreeName}|{item.Period.DisplayValue}"),
            Skills = MergeOrdered(primaryProfile.Skills, fallback.Skills, static item => item.Name),
            Certifications = MergeOrdered(primaryProfile.Certifications, fallback.Certifications, static item => item.Name),
            Projects = MergeOrdered(primaryProfile.Projects, fallback.Projects, static item => item.Title),
            Recommendations = MergeOrdered(primaryProfile.Recommendations, fallback.Recommendations, static item => $"{item.Author.FullName}|{item.Company}|{item.CreatedOn}"),
            ManualSignals = MergeSignals(primaryProfile.ManualSignals, manualSignals)
        };
    }

    private static string? Prefer(string? preferred, string? fallback)
        => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static Uri? Prefer(Uri? preferred, Uri? fallback)
        => preferred ?? fallback;

    private static IReadOnlyDictionary<string, string> MergeSignals(
        IReadOnlyDictionary<string, string> existing,
        IReadOnlyDictionary<string, string>? manualSignals)
    {
        var merged = new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);

        if (manualSignals is null)
        {
            return merged;
        }

        foreach (var pair in manualSignals)
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    private static IReadOnlyList<T> MergeOrdered<T>(
        IReadOnlyList<T> primary,
        IReadOnlyList<T> secondary,
        Func<T, string> keySelector)
    {
        var results = new List<T>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in primary.Concat(secondary))
        {
            var key = keySelector(item);
            if (seen.Add(key))
            {
                results.Add(item);
            }
        }

        return results;
    }
}