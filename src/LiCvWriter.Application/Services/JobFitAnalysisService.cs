using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Application.Services;

public sealed class JobFitAnalysisService(CandidateEvidenceService candidateEvidenceService)
{
    private sealed record FitRequirementContext(
        string Category,
        string Requirement,
        JobRequirementImportance Importance,
        string? SourceLabel = null,
        string? SourceSnippet = null,
        int? SourceConfidence = null,
        JobContextSignal? SourceSignal = null);

    public JobFitAssessment Analyze(
        CandidateProfile candidateProfile,
        JobPostingAnalysis jobPosting,
        CompanyResearchProfile? companyProfile,
        ApplicantDifferentiatorProfile? differentiatorProfile = null)
    {
        var requirements = BuildRequirements(jobPosting, companyProfile);

        // Append inferred (hidden) requirements as nice-to-have, avoiding duplicates.
        if (jobPosting.InferredRequirements.Count > 0)
        {
            var existingLabels = requirements
                .Select(static r => r.Requirement)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var inferred = jobPosting.InferredRequirements
                .Where(label => !existingLabels.Contains(label))
                .Select(static label => new FitRequirementContext("Inferred", label, JobRequirementImportance.NiceToHave))
                .ToArray();
            if (inferred.Length > 0)
            {
                requirements = [.. requirements, .. inferred];
            }
        }

        if (requirements.Count == 0)
        {
            return JobFitAssessment.Empty;
        }

        var evidenceCatalog = candidateEvidenceService.BuildCatalog(candidateProfile);
        var assessments = requirements
            .Select(requirement => AssessRequirement(requirement, evidenceCatalog, differentiatorProfile))
            .ToArray();

        return JobFitScoring.BuildAssessment(assessments);
    }

    private static IReadOnlyList<FitRequirementContext> BuildRequirements(
        JobPostingAnalysis jobPosting,
        CompanyResearchProfile? companyProfile)
    {
        var sourceBackedRequirements = jobPosting.Signals
            .Concat(companyProfile?.Signals ?? Array.Empty<JobContextSignal>())
            .Where(static signal => !string.IsNullOrWhiteSpace(signal.Requirement))
            .Select(static signal => new FitRequirementContext(
                signal.Category,
                signal.Requirement,
                signal.Importance,
                signal.SourceLabel,
                signal.SourceSnippet,
                signal.Confidence,
                signal))
            .DistinctBy(static requirement => $"{requirement.Importance}:{requirement.Requirement}", StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sourceBackedRequirements.Length > 0)
        {
            return sourceBackedRequirements;
        }

        var mustHave = jobPosting.MustHaveThemes.Select(static requirement => (Category: "Must have", Requirement: requirement, Importance: JobRequirementImportance.MustHave));
        var niceToHave = jobPosting.NiceToHaveThemes.Select(static requirement => (Category: "Nice to have", Requirement: requirement, Importance: JobRequirementImportance.NiceToHave));
        var cultural = jobPosting.CulturalSignals
            .Concat(companyProfile?.CulturalSignals ?? Array.Empty<string>())
            .Select(static requirement => (Category: "Culture", Requirement: requirement, Importance: JobRequirementImportance.Cultural));

        var requirements = mustHave.Concat(niceToHave).Concat(cultural)
            .Where(static requirement => !string.IsNullOrWhiteSpace(requirement.Requirement))
            .DistinctBy(static requirement => $"{requirement.Importance}:{requirement.Requirement}", StringComparer.OrdinalIgnoreCase)
            .Select(static requirement => new FitRequirementContext(requirement.Category, requirement.Requirement, requirement.Importance))
            .ToArray();

        if (requirements.Length > 0)
        {
            return requirements;
        }

        var fallbackSignals = JobSignalExtractor.Extract(string.Join(Environment.NewLine, new[]
        {
            jobPosting.Summary,
            string.Join(", ", companyProfile?.Differentiators ?? Array.Empty<string>()),
            companyProfile?.Summary
        }));

        return fallbackSignals.MustHaveThemes.Select(static requirement => new FitRequirementContext("Must have", requirement, JobRequirementImportance.MustHave))
            .Concat(fallbackSignals.NiceToHaveThemes.Select(static requirement => new FitRequirementContext("Nice to have", requirement, JobRequirementImportance.NiceToHave)))
            .Concat(fallbackSignals.CulturalSignals.Select(static requirement => new FitRequirementContext("Culture", requirement, JobRequirementImportance.Cultural)))
            .DistinctBy(static requirement => $"{requirement.Importance}:{requirement.Requirement}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static JobRequirementAssessment AssessRequirement(
        FitRequirementContext requirement,
        IReadOnlyList<CandidateEvidenceItem> evidenceCatalog,
        ApplicantDifferentiatorProfile? differentiatorProfile)
    {
        var aliases = ResolveAliases(requirement);
        var supportingEvidence = FindSupportingEvidence(aliases, evidenceCatalog);
        var differentiatorMatch = MatchesDifferentiator(aliases, differentiatorProfile);
        var match = DetermineMatch(supportingEvidence, differentiatorMatch);

        // When a requirement is Missing, check for transferable skills that demonstrate
        // adjacent capability and upgrade to Partial with a specific rationale.
        IReadOnlyList<string> transferableMatches = Array.Empty<string>();
        if (match == JobRequirementMatch.Missing)
        {
            var transferable = TransferableSkillsMatrix.GetTransferableSkills(requirement.Requirement);
            if (transferable.Count > 0)
            {
                var transferableEvidence = transferable
                    .SelectMany(skill =>
                    {
                        var skillAliases = JobSignalExtractor.GetAliases(skill);
                        return FindSupportingEvidence(skillAliases, evidenceCatalog)
                            .Select(e => (Skill: skill, Evidence: e));
                    })
                    .Where(pair => pair.Evidence.Type is CandidateEvidenceType.Experience or CandidateEvidenceType.Project)
                    .ToArray();

                if (transferableEvidence.Length > 0)
                {
                    match = JobRequirementMatch.Partial;
                    transferableMatches = transferableEvidence
                        .Select(static pair => pair.Skill)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToArray();
                    supportingEvidence = transferableEvidence
                        .Select(static pair => pair.Evidence)
                        .DistinctBy(static e => e.Id, StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToArray();
                }
            }
        }

        var rationale = transferableMatches.Count > 0
            ? $"No direct {requirement.Requirement} experience, but transferable skills via {string.Join(", ", transferableMatches)} ({string.Join(", ", supportingEvidence.Select(static e => e.Title).Take(2))})."
            : BuildRationale(match, supportingEvidence, differentiatorMatch);

        return new JobRequirementAssessment(
            requirement.Category,
            requirement.Requirement,
            requirement.Importance,
            match,
            supportingEvidence.Select(static evidence => evidence.Title).Take(3).ToArray(),
            rationale,
            requirement.SourceLabel,
            requirement.SourceSnippet,
            requirement.SourceConfidence);
    }

    private static IReadOnlyList<CandidateEvidenceItem> FindSupportingEvidence(IReadOnlyList<string> aliases, IReadOnlyList<CandidateEvidenceItem> evidenceCatalog)
    {
        return evidenceCatalog
            .Where(evidence => aliases.Any(alias => EvidenceMatchesAlias(evidence, alias)))
            .OrderByDescending(GetEvidenceWeight)
            .ThenBy(static evidence => evidence.Title, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
    }

    private static bool EvidenceMatchesAlias(CandidateEvidenceItem evidence, string alias)
    {
        var evidenceText = JobSignalExtractor.NormalizeText($"{evidence.Title} {evidence.Summary} {string.Join(" ", evidence.Tags)} {evidence.SourceReference}");
        return JobSignalExtractor.ContainsTermOrOverlap(evidenceText, alias);
    }

    private static bool MatchesDifferentiator(IReadOnlyList<string> aliases, ApplicantDifferentiatorProfile? differentiatorProfile)
    {
        if (differentiatorProfile is null || !differentiatorProfile.HasContent)
        {
            return false;
        }

        var differentiatorText = JobSignalExtractor.NormalizeText(string.Join(Environment.NewLine, differentiatorProfile.ToSummaryLines()));
        return aliases
            .Any(alias => JobSignalExtractor.ContainsTermOrOverlap(differentiatorText, alias));
    }

    private static IReadOnlyList<string> ResolveAliases(FitRequirementContext requirement)
        => requirement.SourceSignal is null
            ? JobSignalExtractor.GetAliases(requirement.Requirement)
            : JobSignalExtractor.GetAliases(requirement.SourceSignal);

    private static JobRequirementMatch DetermineMatch(IReadOnlyList<CandidateEvidenceItem> supportingEvidence, bool differentiatorMatch)
    {
        var evidenceStrength = supportingEvidence.Sum(GetEvidenceWeight);
        if (supportingEvidence.Any(static evidence => evidence.Type is CandidateEvidenceType.Experience or CandidateEvidenceType.Project)
            || evidenceStrength >= 70)
        {
            return JobRequirementMatch.Strong;
        }

        if (supportingEvidence.Count > 0 || differentiatorMatch)
        {
            return JobRequirementMatch.Partial;
        }

        return JobRequirementMatch.Missing;
    }

    private static string BuildRationale(JobRequirementMatch match, IReadOnlyList<CandidateEvidenceItem> supportingEvidence, bool differentiatorMatch)
        => match switch
        {
            JobRequirementMatch.Strong when supportingEvidence.Count > 1 => $"Supported by {supportingEvidence[0].Title} and {supportingEvidence.Count - 1} additional evidence item(s).",
            JobRequirementMatch.Strong => $"Supported by {supportingEvidence[0].Title}.",
            JobRequirementMatch.Partial when supportingEvidence.Count > 0 => $"Visible through lighter or indirect evidence such as {supportingEvidence[0].Title}.",
            JobRequirementMatch.Partial when differentiatorMatch => "Aligned with the applicant differentiator profile, but not strongly evidenced in imported work history.",
            _ => "No clear supporting evidence was found in the imported profile."
        };

    private static int GetEvidenceWeight(CandidateEvidenceItem evidence)
        => evidence.Type switch
        {
            CandidateEvidenceType.Experience => 60,
            CandidateEvidenceType.Project => 55,
            CandidateEvidenceType.Recommendation => 50,
            CandidateEvidenceType.Certification => 40,
            CandidateEvidenceType.Summary => 20,
            CandidateEvidenceType.Headline => 18,
            CandidateEvidenceType.Note => 14,
            _ => 0
        };
}