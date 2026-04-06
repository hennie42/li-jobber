using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Application.Services;

public sealed class EvidenceSelectionService(CandidateEvidenceService candidateEvidenceService)
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

        var ranked = evidenceCatalog
            .Select(evidence => Rank(evidence, jobPosting, companyProfile, jobFitAssessment, differentiatorProfile))
            .Where(static evidence => evidence.Score > 0)
            .OrderByDescending(static evidence => evidence.Score)
            .ThenBy(static evidence => evidence.Evidence.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ranked.Length == 0)
        {
            return EvidenceSelectionResult.Empty;
        }

        var selectedIds = SelectDefaults(ranked);
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
        ApplicantDifferentiatorProfile? differentiatorProfile)
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

        return new RankedEvidenceItem(
            evidence,
            score,
            reasons.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray(),
            false);
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

    private static HashSet<string> SelectDefaults(IReadOnlyList<RankedEvidenceItem> rankedEvidence)
    {
        var selected = new List<string>();

        foreach (var evidence in rankedEvidence.Where(static item => item.Evidence.Type is CandidateEvidenceType.Experience or CandidateEvidenceType.Project or CandidateEvidenceType.Recommendation).Take(4))
        {
            selected.Add(evidence.Evidence.Id);
        }

        foreach (var evidence in rankedEvidence)
        {
            if (selected.Count >= 6)
            {
                break;
            }

            if (!selected.Contains(evidence.Evidence.Id, StringComparer.OrdinalIgnoreCase))
            {
                selected.Add(evidence.Evidence.Id);
            }
        }

        return selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int BaseScore(CandidateEvidenceType type)
        => type switch
        {
            CandidateEvidenceType.Experience => 24,
            CandidateEvidenceType.Project => 20,
            CandidateEvidenceType.Recommendation => 18,
            CandidateEvidenceType.Certification => 12,
            CandidateEvidenceType.Skill => 10,
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
}