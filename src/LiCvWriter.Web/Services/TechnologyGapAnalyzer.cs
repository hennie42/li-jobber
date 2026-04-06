using System.Text;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Web.Services;

public static class TechnologyGapAnalyzer
{
    private static readonly TechnologySignal[] Signals =
    [
        new("Generative AI", ["generative ai", "gen ai", "genai"]),
        new("LLMs", ["llm", "llms", "large language model", "large language models"]),
        new("RAG", ["rag", "retrieval augmented generation", "retrieval-augmented generation"]),
        new("Agentic AI", ["agentic ai", "ai agents", "agent based ai", "agent-based ai"]),
        new("Prompt engineering", ["prompt engineering", "prompt design"]),
        new("Semantic Kernel", ["semantic kernel"]),
        new("LangChain", ["langchain"]),
        new("Vector databases", ["vector database", "vector databases", "embedding", "embeddings"]),
        new("Python", ["python"]),
        new("C#", ["c#", "c sharp"]),
        new(".NET", [".net", "dotnet", "asp.net"]),
        new("Azure", ["azure", "azure ai", "azure openai"]),
        new("AWS", ["aws", "amazon web services"]),
        new("Docker", ["docker", "containers"]),
        new("Kubernetes", ["kubernetes", "k8s"]),
        new("Terraform", ["terraform", "infrastructure as code"]),
        new("MLOps", ["mlops", "model ops"]),
        new("Databricks", ["databricks"]),
        new("Apache Spark", ["spark", "apache spark"]),
        new("Kafka", ["kafka", "apache kafka"]),
        new("GitHub Actions", ["github actions", "github workflows"]),
        new("TypeScript", ["typescript"]),
        new("React", ["react", "reactjs"]),
        new("GraphQL", ["graphql"])
    ];

    public static TechnologyGapAssessment Analyze(CandidateProfile? candidateProfile, JobPostingAnalysis? jobPosting, CompanyResearchProfile? companyProfile)
    {
        if (candidateProfile is null || jobPosting is null)
        {
            return TechnologyGapAssessment.Empty;
        }

        var resolvedSignals = BuildResolvedTechnologySignals(jobPosting, companyProfile);
        var jobContext = NormalizeText(BuildJobContext(jobPosting, companyProfile));
        if (string.IsNullOrWhiteSpace(jobContext) && resolvedSignals.All(static signal => !signal.HasSourceMatch))
        {
            return TechnologyGapAssessment.Empty;
        }

        var detected = resolvedSignals
            .Where(signal => signal.HasSourceMatch || signal.MatchAliases.Any(alias => ContainsNormalizedTerm(jobContext, alias)))
            .Select(signal => signal.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (detected.Length == 0)
        {
            return TechnologyGapAssessment.Empty;
        }

        var profileContext = NormalizeText(BuildProfileContext(candidateProfile));
        var missing = resolvedSignals
            .Where(signal => detected.Contains(signal.Label, StringComparer.OrdinalIgnoreCase))
            .Where(signal => !signal.MatchAliases.Any(alias => ContainsNormalizedTerm(profileContext, alias)))
            .Select(signal => signal.Label)
            .ToArray();

        return new TechnologyGapAssessment(detected, missing);
    }

    private static ResolvedTechnologySignal[] BuildResolvedTechnologySignals(JobPostingAnalysis jobPosting, CompanyResearchProfile? companyProfile)
    {
        var sourceSignals = jobPosting.Signals
            .Concat(companyProfile?.Signals ?? Array.Empty<JobContextSignal>())
            .ToArray();

        return Signals
            .Select(signal => ResolveTechnologySignal(signal, sourceSignals))
            .ToArray();
    }

    private static ResolvedTechnologySignal ResolveTechnologySignal(TechnologySignal technologySignal, IReadOnlyList<JobContextSignal> sourceSignals)
    {
        var sourceAliases = sourceSignals
            .Where(sourceSignal => MapsToTechnology(sourceSignal, technologySignal))
            .SelectMany(GetSignalTerms)
            .ToArray();

        return new ResolvedTechnologySignal(
            technologySignal.Label,
            MergeAliases(technologySignal.Label, technologySignal.Aliases, sourceAliases),
            sourceAliases.Length > 0);
    }

    private static bool MapsToTechnology(JobContextSignal sourceSignal, TechnologySignal technologySignal)
        => GetSignalTerms(sourceSignal)
            .Any(sourceTerm => GetTechnologyTerms(technologySignal).Any(technologyTerm => TermsOverlap(sourceTerm, technologyTerm)));

    private static IReadOnlyList<string> GetSignalTerms(JobContextSignal sourceSignal)
        => MergeAliases(sourceSignal.Requirement, sourceSignal.EffectiveAliases);

    private static IReadOnlyList<string> GetTechnologyTerms(TechnologySignal technologySignal)
        => MergeAliases(technologySignal.Label, technologySignal.Aliases);

    private static string BuildJobContext(JobPostingAnalysis jobPosting, CompanyResearchProfile? companyProfile)
    {
        return string.Join(
            Environment.NewLine,
            new[]
            {
                jobPosting.RoleTitle,
                jobPosting.CompanyName,
                jobPosting.Summary,
                string.Join(", ", jobPosting.MustHaveThemes),
                string.Join(", ", jobPosting.NiceToHaveThemes),
                companyProfile?.Summary,
                string.Join(", ", companyProfile?.Differentiators ?? Array.Empty<string>())
            }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildProfileContext(CandidateProfile candidateProfile)
    {
        var builder = new StringBuilder();
        builder.AppendLine(candidateProfile.Headline);
        builder.AppendLine(candidateProfile.Summary);
        builder.AppendLine(string.Join(", ", candidateProfile.Skills.Select(static skill => skill.Name)));
        builder.AppendLine(string.Join(Environment.NewLine, candidateProfile.Experience.Select(static role => $"{role.Title} {role.Description}")));
        builder.AppendLine(string.Join(Environment.NewLine, candidateProfile.Projects.Select(static project => $"{project.Title} {project.Description}")));
        builder.AppendLine(string.Join(Environment.NewLine, candidateProfile.Certifications.Select(static certification => certification.Name)));

        foreach (var signal in candidateProfile.ManualSignals.Values)
        {
            builder.AppendLine(signal);
        }

        return builder.ToString();
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append(' ');

        var previousWasSeparator = true;
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character == '#' || character == '+' || character == '.')
            {
                builder.Append(character);
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator)
            {
                builder.Append(' ');
                previousWasSeparator = true;
            }
        }

        builder.Append(' ');
        return builder.ToString();
    }

    private static bool ContainsNormalizedTerm(string normalizedHaystack, string alias)
        => normalizedHaystack.Contains(NormalizeText(alias), StringComparison.Ordinal);

    private static bool TermsOverlap(string left, string right)
    {
        var normalizedLeft = NormalizeText(left);
        var normalizedRight = NormalizeText(right);

        return normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal)
            || normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal);
    }

    private static string[] MergeAliases(string label, params IEnumerable<string>[] aliasSets)
        => aliasSets
            .Prepend([label])
            .SelectMany(static aliases => aliases)
            .Where(static alias => !string.IsNullOrWhiteSpace(alias))
            .Select(static alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private sealed record TechnologySignal(string Label, string[] Aliases);

    private sealed record ResolvedTechnologySignal(string Label, IReadOnlyList<string> MatchAliases, bool HasSourceMatch);
}