using System.Text.RegularExpressions;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Application.Services;

public sealed class EvidenceSelectionService(CandidateEvidenceService candidateEvidenceService, int defaultSelectedEvidenceCount = 30)
{
    public EvidenceSelectionResult Build(
        CandidateProfile candidateProfile,
        JobPostingAnalysis jobPosting,
        CompanyResearchProfile? companyProfile,
        JobFitAssessment jobFitAssessment,
        ApplicantDifferentiatorProfile? differentiatorProfile = null)
    {
        var evidenceCatalog = candidateEvidenceService.BuildCatalog(candidateProfile);
        if (evidenceCatalog.Count == 0)
        {
            return EvidenceSelectionResult.Empty;
        }

        var recencyMap = BuildRecencyMap(candidateProfile);

        var ranked = evidenceCatalog
            .Select(evidence => Rank(evidence, jobPosting, companyProfile, jobFitAssessment, differentiatorProfile, recencyMap))
            .Where(static evidence => evidence.Score > 0)
            .OrderByDescending(static evidence => evidence.Score)
            .ThenBy(static evidence => evidence.Evidence.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ranked.Length == 0)
        {
            return EvidenceSelectionResult.Empty;
        }

        var mustHaveThemes = jobFitAssessment.Requirements
            .Where(static r => r.Importance == JobRequirementImportance.MustHave)
            .Select(static r => r.Requirement)
            .ToArray();
        var selectedIds = SelectDiversified(ranked, defaultSelectedEvidenceCount, mustHaveThemes);
        var selectedRanked = ranked
            .Select(item => item with { IsSelected = selectedIds.Contains(item.Evidence.Id) })
            .ToArray();

        return new EvidenceSelectionResult(selectedRanked);
    }

    private static RankedEvidenceItem Rank(
        CandidateEvidenceItem evidence,
        JobPostingAnalysis jobPosting,
        CompanyResearchProfile? companyProfile,
        JobFitAssessment jobFitAssessment,
        ApplicantDifferentiatorProfile? differentiatorProfile,
        IReadOnlyDictionary<string, int?> recencyMap)
    {
        var score = BaseScore(evidence.Type);
        var reasons = new List<string>();
        var contextTerms = GetContextTerms(jobPosting, companyProfile);

        foreach (var requirement in jobFitAssessment.Requirements)
        {
            if (!MatchesRequirement(evidence, ResolveAliases(requirement.Requirement, jobPosting, companyProfile)))
            {
                continue;
            }

            score += requirement.Importance switch
            {
                JobRequirementImportance.MustHave => 18,
                JobRequirementImportance.NiceToHave => 10,
                JobRequirementImportance.Cultural => 12,
                _ => 0
            };
            reasons.Add($"Supports {FormatImportance(requirement.Importance)}: {requirement.Requirement}");
        }

        if (MatchesNarrative(evidence, differentiatorProfile))
        {
            score += 8;
            reasons.Add("Aligns with the applicant narrative.");
        }

        if (evidence.Type == CandidateEvidenceType.Recommendation)
        {
            score += 6;
            reasons.Add("Third-party validation.");
        }

        if (evidence.Type == CandidateEvidenceType.Experience && !string.IsNullOrWhiteSpace(evidence.SourceReference))
        {
            score += 4;
            reasons.Add("Concrete work-history evidence.");
        }

        if (contextTerms.Any(term => MatchText(evidence, term)))
        {
            score += 4;
            reasons.Add("Matches the broader target context.");
        }

        // Recency weighting: recent evidence is more compelling to recruiters.
        var recencyBonus = GetRecencyBonus(evidence.Id, recencyMap);
        if (recencyBonus > 0)
        {
            score += recencyBonus;
            reasons.Add("Recent and relevant experience.");
        }

        // Recommendation specificity: longer, more specific recommendations score higher.
        if (evidence.Type == CandidateEvidenceType.Recommendation)
        {
            var specificity = ScoreRecommendationStrength(evidence.Summary);
            score += specificity;
            if (specificity >= 4)
            {
                reasons.Add("Highly specific recommendation with concrete detail.");
            }
        }

        return new RankedEvidenceItem(
            evidence,
            score,
            reasons.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray(),
            false);
    }

    /// <summary>
    /// Scores a recommendation's strength based on specificity indicators.
    /// Returns 0-6 bonus points.
    /// </summary>
    internal static int ScoreRecommendationStrength(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var score = 0;

        // Length: longer recommendations tend to be more substantive.
        if (text.Length > 300) score += 2;
        else if (text.Length > 150) score += 1;

        // Concrete indicators: numbers, metrics, specific technologies.
        if (Regex.IsMatch(text, @"\d+[%+xX]|\d+\s*(years?|months?|team|people|projects?)", RegexOptions.IgnoreCase))
        {
            score += 2;
        }

        // Action/impact language indicating specific observations.
        var impactKeywords = new[] { "led", "delivered", "built", "architected", "transformed", "improved", "drove", "scaled", "mentored", "spearheaded" };
        if (impactKeywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            score += 2;
        }

        return Math.Min(score, 6);
    }

    private static bool MatchesRequirement(CandidateEvidenceItem evidence, IReadOnlyList<string> aliases)
        => aliases
            .Any(alias => MatchText(evidence, alias));

    private static IReadOnlyList<string> ResolveAliases(
        string requirement,
        JobPostingAnalysis jobPosting,
        CompanyResearchProfile? companyProfile)
    {
        var sourceSignal = jobPosting.Signals
            .Concat(companyProfile?.Signals ?? Array.Empty<JobContextSignal>())
            .FirstOrDefault(signal => signal.Requirement.Equals(requirement, StringComparison.OrdinalIgnoreCase));

        return sourceSignal is null
            ? JobSignalExtractor.GetAliases(requirement)
            : JobSignalExtractor.GetAliases(sourceSignal);
    }

    private static IReadOnlyList<string> GetContextTerms(JobPostingAnalysis jobPosting, CompanyResearchProfile? companyProfile)
    {
        var sourceSignals = jobPosting.Signals
            .Concat(companyProfile?.Signals ?? Array.Empty<JobContextSignal>())
            .ToArray();

        if (sourceSignals.Length > 0)
        {
            return sourceSignals
                .SelectMany(JobSignalExtractor.GetAliases)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return JobSignalExtractor.DetectThemeLabels(string.Join(Environment.NewLine, new[]
        {
            jobPosting.Summary,
            companyProfile?.Summary,
            string.Join(", ", companyProfile?.Differentiators ?? Array.Empty<string>())
        }));
    }

    private static bool MatchesNarrative(CandidateEvidenceItem evidence, ApplicantDifferentiatorProfile? differentiatorProfile)
    {
        if (differentiatorProfile is null || !differentiatorProfile.HasContent)
        {
            return false;
        }

        var narrativeTokens = differentiatorProfile.ToSummaryLines()
            .SelectMany(static line => line.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(static token => token.Length >= 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return narrativeTokens.Any(token => MatchText(evidence, token));
    }

    private static bool MatchText(CandidateEvidenceItem evidence, string term)
    {
        var evidenceText = JobSignalExtractor.NormalizeText($"{evidence.Title} {evidence.Summary} {string.Join(" ", evidence.Tags)} {evidence.SourceReference}");
        return JobSignalExtractor.ContainsTermOrOverlap(evidenceText, term);
    }

    private const int MaxPerSourceEntry = 5;
    private const int MaxPerEvidenceType = 8;

    /// <summary>
    /// Selects up to <paramref name="maxCount"/> evidence items while enforcing diversity:
    /// max <see cref="MaxPerSourceEntry"/> items per source experience/project,
    /// max <see cref="MaxPerEvidenceType"/> from any one evidence type,
    /// and ensures at least one recommendation and one certification when available.
    /// Also ensures every must-have theme has at least one backing evidence item.
    /// </summary>
    private static HashSet<string> SelectDiversified(
        IReadOnlyList<RankedEvidenceItem> rankedEvidence,
        int maxCount,
        IReadOnlyList<string> mustHaveThemes)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var typeCount = new Dictionary<CandidateEvidenceType, int>();
        var sourceCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        bool TryAdd(RankedEvidenceItem item)
        {
            if (selected.Contains(item.Evidence.Id))
            {
                return false;
            }

            var type = item.Evidence.Type;
            var typeUsed = typeCount.GetValueOrDefault(type);
            if (typeUsed >= MaxPerEvidenceType)
            {
                return false;
            }

            var source = item.Evidence.SourceReference ?? item.Evidence.Id;
            var sourceUsed = sourceCount.GetValueOrDefault(source);
            if (sourceUsed >= MaxPerSourceEntry)
            {
                return false;
            }

            selected.Add(item.Evidence.Id);
            typeCount[type] = typeUsed + 1;
            sourceCount[source] = sourceUsed + 1;
            return true;
        }

        // Pass 1: ensure at least one recommendation if available.
        var topRecommendation = rankedEvidence.FirstOrDefault(static r => r.Evidence.Type == CandidateEvidenceType.Recommendation);
        if (topRecommendation is not null)
        {
            TryAdd(topRecommendation);
        }

        // Pass 2: ensure at least one certification if available.
        var topCertification = rankedEvidence.FirstOrDefault(static r => r.Evidence.Type == CandidateEvidenceType.Certification);
        if (topCertification is not null)
        {
            TryAdd(topCertification);
        }

        // Pass 3: ensure at least one evidence item per must-have theme.
        foreach (var theme in mustHaveThemes)
        {
            if (selected.Count >= maxCount)
            {
                break;
            }

            var alreadyCovered = rankedEvidence
                .Any(r => selected.Contains(r.Evidence.Id)
                    && MatchText(r.Evidence, theme));
            if (alreadyCovered)
            {
                continue;
            }

            var bestForTheme = rankedEvidence
                .FirstOrDefault(r => !selected.Contains(r.Evidence.Id) && MatchText(r.Evidence, theme));
            if (bestForTheme is not null)
            {
                TryAdd(bestForTheme);
            }
        }

        // Pass 4: fill remaining slots by rank, respecting diversity caps.
        foreach (var item in rankedEvidence)
        {
            if (selected.Count >= maxCount)
            {
                break;
            }

            TryAdd(item);
        }

        return selected;
    }

    private static int BaseScore(CandidateEvidenceType type)
        => type switch
        {
            CandidateEvidenceType.Experience => 24,
            CandidateEvidenceType.Project => 20,
            CandidateEvidenceType.Recommendation => 18,
            CandidateEvidenceType.Certification => 12,
            CandidateEvidenceType.Summary => 8,
            CandidateEvidenceType.Headline => 6,
            CandidateEvidenceType.Note => 6,
            _ => 0
        };

    private static string FormatImportance(JobRequirementImportance importance)
        => importance switch
        {
            JobRequirementImportance.MustHave => "must-have",
            JobRequirementImportance.NiceToHave => "nice-to-have",
            JobRequirementImportance.Cultural => "cultural signal",
            _ => "requirement"
        };

    /// <summary>
    /// Builds a lookup from evidence ID to the most recent year associated with
    /// that evidence item (experience end year, project end year, etc.).
    /// </summary>
    private static IReadOnlyDictionary<string, int?> BuildRecencyMap(CandidateProfile candidateProfile)
    {
        var map = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var currentYear = DateTime.UtcNow.Year;

        foreach (var role in candidateProfile.Experience)
        {
            var id = $"experience:{NormalizeEvidenceId($"{role.CompanyName}-{role.Title}-{role.Period.DisplayValue}")}";
            var endYear = role.Period.FinishedOn?.Year ?? currentYear;
            map[id] = endYear;
        }

        foreach (var project in candidateProfile.Projects)
        {
            var id = $"project:{NormalizeEvidenceId(project.Title)}";
            var endYear = project.Period.FinishedOn?.Year ?? project.Period.StartedOn?.Year;
            map[id] = endYear;
        }

        return map;
    }

    private static string NormalizeEvidenceId(string value)
        => string.Concat(value
            .Trim()
            .ToLowerInvariant()
            .Select(static character => char.IsLetterOrDigit(character) ? character : '-'))
            .Trim('-');

    /// <summary>
    /// Returns a recency bonus: +6 for last 3 years, +3 for 3-7 years, 0 for older.
    /// </summary>
    private static int GetRecencyBonus(string evidenceId, IReadOnlyDictionary<string, int?> recencyMap)
    {
        if (!recencyMap.TryGetValue(evidenceId, out var endYear) || endYear is null)
        {
            return 0;
        }

        var yearsAgo = DateTime.UtcNow.Year - endYear.Value;
        return yearsAgo switch
        {
            <= 3 => 6,
            <= 7 => 3,
            _ => 0
        };
    }
}