using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Application.Services;

public sealed class CandidateEvidenceService
{
    public IReadOnlyList<CandidateEvidenceItem> BuildCatalog(CandidateProfile candidateProfile)
    {
        var evidence = new List<CandidateEvidenceItem>();

        if (!string.IsNullOrWhiteSpace(candidateProfile.Headline))
        {
            evidence.Add(new CandidateEvidenceItem(
                "headline",
                CandidateEvidenceType.Headline,
                "Profile headline",
                candidateProfile.Headline.Trim(),
                BuildTags(candidateProfile.Headline)));
        }

        if (!string.IsNullOrWhiteSpace(candidateProfile.Summary))
        {
            evidence.Add(new CandidateEvidenceItem(
                "summary",
                CandidateEvidenceType.Summary,
                "Profile summary",
                candidateProfile.Summary.Trim(),
                BuildTags(candidateProfile.Summary)));
        }

        foreach (var role in candidateProfile.Experience)
        {
            evidence.Add(new CandidateEvidenceItem(
                BuildExperienceId(role),
                CandidateEvidenceType.Experience,
                $"{role.Title} @ {role.CompanyName}".Trim(' ', '@'),
                BuildExperienceSummary(role),
                BuildTags($"{role.Title} {role.CompanyName} {role.Description}", role.Title, role.CompanyName),
                role.Period.DisplayValue));
        }

        foreach (var project in candidateProfile.Projects)
        {
            evidence.Add(new CandidateEvidenceItem(
                $"project:{NormalizeId(project.Title)}",
                CandidateEvidenceType.Project,
                project.Title,
                project.Description ?? project.Title,
                BuildTags($"{project.Title} {project.Description}", project.Title),
                project.Period.DisplayValue));
        }

        foreach (var recommendation in candidateProfile.Recommendations)
        {
            evidence.Add(new CandidateEvidenceItem(
                $"recommendation:{NormalizeId($"{recommendation.Author.FullName}-{recommendation.Company}-{recommendation.CreatedOn:O}")}",
                CandidateEvidenceType.Recommendation,
                $"Recommendation from {recommendation.Author.FullName}",
                recommendation.Text,
                BuildTags($"{recommendation.Author.FullName} {recommendation.Company} {recommendation.JobTitle} {recommendation.Text}", recommendation.Company, recommendation.JobTitle),
                recommendation.Company));
        }

        foreach (var certification in candidateProfile.Certifications)
        {
            evidence.Add(new CandidateEvidenceItem(
                $"certification:{NormalizeId(certification.Name)}",
                CandidateEvidenceType.Certification,
                certification.Name,
                certification.Authority ?? certification.Name,
                BuildTags($"{certification.Name} {certification.Authority}", certification.Name),
                certification.Period.DisplayValue));
        }

        foreach (var pair in candidateProfile.ManualSignals)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            evidence.Add(new CandidateEvidenceItem(
                $"note:{NormalizeId(pair.Key)}",
                CandidateEvidenceType.Note,
                pair.Key,
                pair.Value,
                BuildTags($"{pair.Key} {pair.Value}", pair.Key)));
        }

        return evidence
            .GroupBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(GetEvidenceRichness)
                .ThenByDescending(static item => item.Title.Length)
                .First())
            .ToArray();
    }

    private static string BuildExperienceId(ExperienceEntry role)
        => $"experience:{NormalizeId($"{role.CompanyName}-{role.Title}-{role.Period.DisplayValue}")}";

    private static string BuildExperienceSummary(ExperienceEntry role)
    {
        if (string.IsNullOrWhiteSpace(role.Description))
        {
            return string.IsNullOrWhiteSpace(role.Period.DisplayValue)
                ? role.CompanyName
                : $"{role.CompanyName} | {role.Period.DisplayValue}";
        }

        return string.IsNullOrWhiteSpace(role.Period.DisplayValue)
            ? role.Description.Trim()
            : $"{role.Description.Trim()} ({role.Period.DisplayValue})";
    }

    private static IReadOnlyList<string> BuildTags(string? text, params string?[] explicitTags)
        => explicitTags
            .Append(text)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Concat(JobSignalExtractor.DetectThemeLabels(text))
            .Concat(JobSignalExtractor.DetectCulturalLabels(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizeId(string? value)
        => string.Concat((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-'))
            .Trim('-');

    private static int GetEvidenceRichness(CandidateEvidenceItem evidence)
        => (string.IsNullOrWhiteSpace(evidence.Summary) ? 0 : 2)
            + (string.IsNullOrWhiteSpace(evidence.SourceReference) ? 0 : 1)
            + evidence.Tags.Count;
}