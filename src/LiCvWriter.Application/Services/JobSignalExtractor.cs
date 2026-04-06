using System.Text;
using System.Text.RegularExpressions;
using LiCvWriter.Core.Jobs;

namespace LiCvWriter.Application.Services;

public sealed record JobSignalExtraction(
    IReadOnlyList<string> MustHaveThemes,
    IReadOnlyList<string> NiceToHaveThemes,
    IReadOnlyList<string> CulturalSignals);

public static class JobSignalExtractor
{
    private static readonly ThemeSignal[] TechnicalThemes =
    [
        new("Generative AI", ["generative ai", "gen ai", "genai"]),
        new("LLMs", ["llm", "llms", "large language model", "large language models"]),
        new("RAG", ["rag", "retrieval augmented generation", "retrieval-augmented generation"]),
        new("Agentic AI", ["agentic ai", "ai agents", "agent-based ai", "agent based ai"]),
        new("Prompt engineering", ["prompt engineering", "prompt design"]),
        new("Semantic Kernel", ["semantic kernel"]),
        new("LangChain", ["langchain"]),
        new("Vector databases", ["vector database", "vector databases", "embedding", "embeddings"]),
        new("Python", ["python"]),
        new("C#", ["c#", "c sharp"]),
        new(".NET", [".net", "dotnet", "asp.net"]),
        new("Azure", ["azure", "azure openai", "azure ai"]),
        new("AWS", ["aws", "amazon web services"]),
        new("Docker", ["docker", "containerization", "containers"]),
        new("Kubernetes", ["kubernetes", "k8s"]),
        new("Terraform", ["terraform", "infrastructure as code"]),
        new("MLOps", ["mlops", "model ops"]),
        new("Databricks", ["databricks"]),
        new("Apache Spark", ["apache spark", "spark"]),
        new("Kafka", ["kafka", "apache kafka"]),
        new("GitHub Actions", ["github actions", "github workflows"]),
        new("TypeScript", ["typescript"]),
        new("React", ["react", "reactjs"]),
        new("Architecture", ["architecture", "architect", "solution design", "solution architecture"]),
        new("Consulting", ["consulting", "consultant", "advisory"]),
        new("Client leadership", ["client-facing", "client facing", "stakeholder management", "stakeholder engagement"]),
        new("Communication", ["communication", "written communication", "verbal communication", "communicator"]),
        new("Workshop facilitation", ["workshop facilitation", "facilitation", "facilitating workshops", "facilitate workshops"]),
        new("Presentation", ["presentation", "presentations", "storytelling", "presenting"]),
        new("Pre-sales", ["pre-sales", "presales", "proposal writing", "bids", "bid work"]),
        new("Delivery leadership", ["delivery leadership", "delivery lead", "engagement lead", "project leadership"]),
        new("Change management", ["change management", "organizational change", "change enablement"]),
        new("Product mindset", ["product mindset", "product thinking", "customer outcomes", "user needs"]),
        new("Leadership", ["technical leadership", "leadership", "lead engineer", "lead architect", "mentoring"]),
        new("Regulated environments", ["regulated", "compliance", "governance", "risk", "financial services", "healthcare"]),
        new("Product discovery", ["product discovery", "discovery workshops", "product strategy"]),
        new("Data platforms", ["data platform", "data engineering", "analytics platform"]),
        new("Security", ["security", "secure by design", "identity", "iam"]),
        new("Cloud migration", ["cloud migration", "modernization", "migration"]),
        new("Knowledge sharing", ["knowledge sharing", "enablement", "coaching", "workshops"]),
        new("Quality engineering", ["quality", "testing", "maintainability", "reliability"])
    ];

    private static readonly ThemeSignal[] CulturalThemes =
    [
        new("Trust", ["trust", "trusted", "integrity"]),
        new("Craftsmanship", ["craftsmanship", "quality", "care for the craft"]),
        new("Knowledge sharing", ["knowledge sharing", "teaching", "enablement", "community"]),
        new("Pragmatism", ["pragmatic", "pragmatism", "practical"]),
        new("Collaboration", ["collaboration", "collaborative", "team player", "cross-functional"]),
        new("Ownership", ["ownership", "accountability", "own outcomes"]),
        new("Curiosity", ["curiosity", "learning mindset", "continuous learning"]),
        new("Client-facing", ["client-facing", "client facing", "stakeholder management", "stakeholder engagement"]),
        new("Leadership", ["leadership", "mentoring", "coach", "coaching"]),
        new("Adaptability", ["adaptability", "adaptable", "comfortable with ambiguity"]),
        new("Regulated delivery", ["regulated", "governance", "compliance", "risk aware"]),
        new("Experimentation", ["experimentation", "iterate", "prototype", "prototype quickly"])
    ];

    private static readonly string[] MustHaveMarkers =
    [
        "must have",
        "required",
        "requirements",
        "you have",
        "you will bring",
        "what you bring",
        "your profile",
        "proven experience",
        "hands-on experience",
        "strong experience",
        "we are looking for",
        "qualifications",
        "you should have",
        "we expect"
    ];

    private static readonly string[] NiceToHaveMarkers =
    [
        "nice to have",
        "preferred",
        "bonus",
        "would be a plus",
        "desirable",
        "ideally",
        "considered a plus",
        "helpful"
    ];

    private static readonly string[] CultureMarkers =
    [
        "we value",
        "our values",
        "culture",
        "ways of working",
        "you thrive",
        "working style"
    ];

    private static readonly string[] LeadingFillerWords =
    [
        "strong",
        "solid",
        "excellent",
        "proven",
        "hands-on",
        "hands on",
        "practical",
        "good"
    ];

    public static JobSignalExtraction Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JobSignalExtraction(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        }

        var mustHave = new List<string>();
        var niceToHave = new List<string>();
        var discovered = new List<string>();
        var chunks = Regex.Split(text, @"(?:\r?\n)+|(?<=[\.!?;:])\s+")
            .Select(static chunk => chunk.Trim())
            .Where(static chunk => !string.IsNullOrWhiteSpace(chunk));

        foreach (var chunk in chunks)
        {
            var detected = DetectThemes(chunk, TechnicalThemes);
            var explicitRequirements = ExtractExplicitRequirements(chunk);

            discovered.AddRange(detected);

            if (ContainsMarker(chunk, MustHaveMarkers))
            {
                mustHave.AddRange(detected.Count > 0 ? detected : explicitRequirements);
                continue;
            }

            if (ContainsMarker(chunk, NiceToHaveMarkers))
            {
                niceToHave.AddRange(detected.Count > 0 ? detected : explicitRequirements);
                continue;
            }

            if (detected.Count == 0)
            {
                continue;
            }
        }

        var distinctDiscovered = Distinct(discovered);
        var distinctMustHave = Distinct(mustHave);
        var distinctNiceToHave = Distinct(niceToHave.Where(theme => !distinctMustHave.Contains(theme, StringComparer.OrdinalIgnoreCase)));

        if (distinctMustHave.Count == 0)
        {
            distinctMustHave = distinctDiscovered.Take(6).ToArray();
        }

        if (distinctNiceToHave.Count == 0)
        {
            distinctNiceToHave = distinctDiscovered
                .Where(theme => !distinctMustHave.Contains(theme, StringComparer.OrdinalIgnoreCase))
                .Take(4)
                .ToArray();
        }

        var culturalSignals = DetectThemes(text, CulturalThemes)
            .Concat(ExtractExplicitCultureSignals(chunks))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new JobSignalExtraction(distinctMustHave, distinctNiceToHave, culturalSignals);
    }

    public static IReadOnlyList<string> DetectThemeLabels(string? text)
        => DetectThemes(text, TechnicalThemes);

    public static IReadOnlyList<string> DetectCulturalLabels(string? text)
        => DetectThemes(text, CulturalThemes);

    public static IReadOnlyList<string> GetAliases(string label)
    {
        var match = TechnicalThemes.Concat(CulturalThemes)
            .FirstOrDefault(theme => theme.Label.Equals(label, StringComparison.OrdinalIgnoreCase));

        return match is null
            ? [label]
            : DistinctAliases(match.Label, match.Aliases);
    }

    public static IReadOnlyList<string> GetAliases(JobContextSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        return signal.HasAliases
            ? DistinctAliases(signal.Requirement, signal.EffectiveAliases)
            : GetAliases(signal.Requirement);
    }

    public static string NormalizeText(string? value)
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

    public static IReadOnlyList<string> TokenizeTerms(string? value, int minimumLength = 4)
        => NormalizeText(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= minimumLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool ContainsTermOrOverlap(string normalizedHaystack, string term)
    {
        var normalizedTerm = NormalizeText(term);
        if (string.IsNullOrWhiteSpace(normalizedTerm))
        {
            return false;
        }

        if (normalizedHaystack.Contains(normalizedTerm, StringComparison.Ordinal))
        {
            return true;
        }

        var tokens = TokenizeTerms(term);
        if (tokens.Count < 2)
        {
            return false;
        }

        var overlap = tokens.Count(token => normalizedHaystack.Contains(NormalizeText(token), StringComparison.Ordinal));
        return overlap >= Math.Min(2, tokens.Count);
    }

    private static IReadOnlyList<string> DetectThemes(string? text, IEnumerable<ThemeSignal> signals)
    {
        var normalizedText = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return Array.Empty<string>();
        }

        return signals
            .Where(signal => signal.Aliases.Any(alias => normalizedText.Contains(NormalizeText(alias), StringComparison.Ordinal)))
            .Select(signal => signal.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ContainsMarker(string text, IEnumerable<string> markers)
    {
        var normalizedText = NormalizeText(text);
        return markers.Any(marker => normalizedText.Contains(NormalizeText(marker), StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> ExtractExplicitRequirements(string chunk)
    {
        var normalized = chunk.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Array.Empty<string>();
        }

        normalized = Regex.Replace(normalized, @"^[\-\*\u2022\d\.\)\s]+", string.Empty);
        normalized = RemoveLeadingMarker(normalized, MustHaveMarkers.Concat(NiceToHaveMarkers));
        normalized = Regex.Replace(normalized, @"\b(experience|skills|skill set|background|knowledge|capability|capabilities)\b", string.Empty, RegexOptions.IgnoreCase);

        var parts = normalized
            .Split([",", " and ", " or ", "/"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TrimRequirementPhrase)
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .Where(static part => part.Length is >= 4 and <= 50)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return parts;
    }

    private static IReadOnlyList<string> ExtractExplicitCultureSignals(IEnumerable<string> chunks)
        => chunks
            .Where(chunk => ContainsMarker(chunk, CultureMarkers))
            .SelectMany(ExtractExplicitRequirements)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string RemoveLeadingMarker(string chunk, IEnumerable<string> markers)
    {
        var updated = chunk;
        foreach (var marker in markers.OrderByDescending(static marker => marker.Length))
        {
            updated = Regex.Replace(updated, $@"^.*?\b{Regex.Escape(marker)}\b[:\-\s]*", string.Empty, RegexOptions.IgnoreCase);
        }

        return updated;
    }

    private static string TrimRequirementPhrase(string value)
    {
        var trimmed = value.Trim().Trim('.', ';', ':');

        foreach (var filler in LeadingFillerWords)
        {
            if (trimmed.StartsWith(filler + " ", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[(filler.Length + 1)..];
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> DistinctAliases(string label, IEnumerable<string> aliases)
        => aliases
            .Prepend(label)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private sealed record ThemeSignal(string Label, string[] Aliases);
}