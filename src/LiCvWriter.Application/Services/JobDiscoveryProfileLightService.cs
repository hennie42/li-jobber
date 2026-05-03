using LiCvWriter.Application.Models;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Application.Services;

public sealed class JobDiscoveryProfileLightService
{
    public JobDiscoveryProfileLight Build(
        CandidateProfile? candidateProfile,
        ApplicantDifferentiatorProfile? differentiatorProfile = null)
    {
        if (candidateProfile is null)
        {
            return JobDiscoveryProfileLight.Empty;
        }

        var recentTitles = candidateProfile.Experience
            .Select(static entry => NormalizePhrase(entry.Title))
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Cast<string>()
            .ToArray();

        var skillKeywords = candidateProfile.Skills
            .OrderBy(static skill => skill.Order)
            .Select(static skill => NormalizePhrase(skill.Name))
            .Where(static skill => !string.IsNullOrWhiteSpace(skill))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Cast<string>()
            .ToArray();

        var headline = NormalizePhrase(candidateProfile.Headline) ?? string.Empty;
        var primaryRole = FirstNonEmpty(recentTitles.FirstOrDefault(), headline) ?? string.Empty;
        var targetNarrative = NormalizePhrase(differentiatorProfile?.TargetNarrative) ?? string.Empty;

        var searchTerms = new List<string>();
        AddSearchTerm(searchTerms, primaryRole);

        foreach (var skill in skillKeywords.Take(3))
        {
            AddSearchTerm(searchTerms, skill);
        }

        return new JobDiscoveryProfileLight(
            primaryRole,
            headline,
            NormalizePhrase(candidateProfile.Location) ?? string.Empty,
            NormalizePhrase(candidateProfile.Industry) ?? string.Empty,
            recentTitles,
            skillKeywords,
            targetNarrative,
            string.Join(" ", searchTerms));
    }

    private static void AddSearchTerm(ICollection<string> searchTerms, string? value)
    {
        var normalized = NormalizePhrase(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (searchTerms.Any(existing => existing.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        searchTerms.Add(normalized);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? NormalizePhrase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(" ", value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}