using LiCvWriter.Application.Models;
using System.Text.Json;

namespace LiCvWriter.Application.Services;

/// <summary>
/// Deterministic, offline-friendly quality fixture used to compare Ollama models.
/// A single short job posting is given to the model with strict JSON instructions;
/// the response is scored on (a) parse validity, (b) presence of required keys,
/// and (c) value similarity vs the expected extraction. No LLM-as-judge is used,
/// so scores are reproducible and reviewable.
/// </summary>
public static class ModelBenchmarkFixtures
{
    public const string FixtureId = "job-extraction-short-json";
    public const string FixtureDisplayName = "Short job extraction JSON";
    public const double FixtureWeight = 0.45;

    public const string CompanyFixtureId = "company-context-values-json";
    public const string CompanyFixtureDisplayName = "Company context values JSON";
    public const double CompanyFixtureWeight = 0.30;

    public const string TechnologyGapFixtureId = "technology-gap-signals-json";
    public const string TechnologyGapFixtureDisplayName = "Technology gap signals JSON";
    public const double TechnologyGapFixtureWeight = 0.25;

    public const string SystemPrompt =
        "You extract structured data from short job postings. " +
        "Respond with a single JSON object and no other text. " +
        "Required keys: roleTitle (string), companyName (string), " +
        "mustHaveThemes (array of short lowercase tag strings).";

    public const string UserPrompt =
        "Extract the following job posting:\n\n" +
        "Senior Backend Engineer at Acme Robotics. We need someone with strong " +
        "experience in Go, Kubernetes, and distributed systems. Remote-friendly. " +
        "5+ years required.";

    public const string ExpectedRoleTitle = "Senior Backend Engineer";
    public const string ExpectedCompanyName = "Acme Robotics";
    public static readonly IReadOnlyList<string> ExpectedThemes = new[]
    {
        "go",
        "kubernetes",
        "distributed systems"
    };

    public const string CompanySystemPrompt =
        "You extract structured company context from a short source snippet. " +
        "Respond with a single JSON object and no other text. " +
        "Required keys: name (string), guidingPrinciples (array of short strings), " +
        "differentiators (array of short strings).";

    public const string CompanyUserPrompt =
        "Extract the following company context:\n\n" +
        "Nordic Cloud Guild builds long-term client partnerships through trust, mentoring, " +
        "pragmatic delivery, and open knowledge sharing. The team is known for calm senior advisors " +
        "who guide platform modernization programs.";

    public const string ExpectedCompanyProfileName = "Nordic Cloud Guild";
    public static readonly IReadOnlyList<string> ExpectedGuidingPrinciples = new[]
    {
        "trust",
        "mentoring",
        "pragmatic delivery"
    };

    public static readonly IReadOnlyList<string> ExpectedDifferentiators = new[]
    {
        "knowledge sharing",
        "platform modernization"
    };

    public const string TechnologyGapSystemPrompt =
        "You analyze which technologies are emphasized by the role and which ones appear thin in the candidate evidence. " +
        "Respond with a single JSON object and no other text. " +
        "Required keys: detectedTechnologies (array of short strings), possiblyUnderrepresentedTechnologies (array of short strings).";

    public const string TechnologyGapUserPrompt =
        "Analyze the following role and evidence:\n\n" +
        "Job technologies: RAG, vector search, Kubernetes, LLM evaluation. " +
        "Candidate evidence: built Azure AI Search assistant prototypes and .NET APIs. " +
        "No Kubernetes delivery or LLM evaluation work is described.";

    public static readonly IReadOnlyList<string> ExpectedDetectedTechnologies = new[]
    {
        "RAG",
        "vector search",
        "Kubernetes",
        "LLM evaluation"
    };

    public static readonly IReadOnlyList<string> ExpectedUnderrepresentedTechnologies = new[]
    {
        "Kubernetes",
        "LLM evaluation"
    };

    public static IReadOnlyList<ModelBenchmarkFixtureDefinition> DefaultSuite { get; } =
    [
        new(
            FixtureId,
            LlmPromptCatalog.JobExtractJson,
            FixtureDisplayName,
            FixtureWeight,
            SystemPrompt,
            UserPrompt,
            LlmResponseFormat.Json),
        new(
            CompanyFixtureId,
            LlmPromptCatalog.CompanyExtractJson,
            CompanyFixtureDisplayName,
            CompanyFixtureWeight,
            CompanySystemPrompt,
            CompanyUserPrompt,
            LlmResponseFormat.Json),
        new(
            TechnologyGapFixtureId,
            LlmPromptCatalog.TechGapJson,
            TechnologyGapFixtureDisplayName,
            TechnologyGapFixtureWeight,
            TechnologyGapSystemPrompt,
            TechnologyGapUserPrompt,
            LlmResponseFormat.Json)
    ];

    /// <summary>
    /// Scores a candidate JSON response against the fixture's expected output.
    /// Returns a value in the range [0, 1].
    /// </summary>
    public static double Score(string? candidateJson)
        => Evaluate(candidateJson).Score;

    /// <summary>
    /// Evaluates the default extraction fixture and returns the full fixture result.
    /// </summary>
    public static ModelBenchmarkFixtureResult Evaluate(string? candidateJson)
        => Evaluate(DefaultSuite[0], candidateJson);

    /// <summary>
    /// Evaluates a candidate JSON response and returns the full fixture result.
    /// </summary>
    public static ModelBenchmarkFixtureResult Evaluate(ModelBenchmarkFixtureDefinition fixture, string? candidateJson)
    {
        return fixture.FixtureId switch
        {
            FixtureId => EvaluateJsonObjectFixture(
                fixture,
                candidateJson,
                expectedStrings: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["roleTitle"] = ExpectedRoleTitle,
                    ["companyName"] = ExpectedCompanyName
                },
                expectedArrays: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["mustHaveThemes"] = ExpectedThemes
                }),
            CompanyFixtureId => EvaluateJsonObjectFixture(
                fixture,
                candidateJson,
                expectedStrings: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["name"] = ExpectedCompanyProfileName
                },
                expectedArrays: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["guidingPrinciples"] = ExpectedGuidingPrinciples,
                    ["differentiators"] = ExpectedDifferentiators
                }),
            TechnologyGapFixtureId => EvaluateJsonObjectFixture(
                fixture,
                candidateJson,
                expectedStrings: new Dictionary<string, string>(StringComparer.Ordinal),
                expectedArrays: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["detectedTechnologies"] = ExpectedDetectedTechnologies,
                    ["possiblyUnderrepresentedTechnologies"] = ExpectedUnderrepresentedTechnologies
                }),
            _ => throw new InvalidOperationException($"Unknown benchmark fixture '{fixture.FixtureId}'.")
        };
    }

    /// <summary>
    /// Evaluates a JSON-object fixture against expected string and array fields.
    /// </summary>
    private static ModelBenchmarkFixtureResult EvaluateJsonObjectFixture(
        ModelBenchmarkFixtureDefinition fixture,
        string? candidateJson,
        IReadOnlyDictionary<string, string> expectedStrings,
        IReadOnlyDictionary<string, IReadOnlyList<string>> expectedArrays)
    {
        if (string.IsNullOrWhiteSpace(candidateJson))
        {
            return CreateFixtureResult(
                fixture,
                0.0,
                false,
                0,
                expectedStrings.Count + expectedArrays.Count,
                0.0,
                ["Candidate output was empty."]);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(candidateJson);
        }
        catch (JsonException)
        {
            return CreateFixtureResult(
                fixture,
                0.0,
                false,
                0,
                expectedStrings.Count + expectedArrays.Count,
                0.0,
                ["Candidate output was not valid JSON."]);
        }

        using (document)
        {
            const double parseScore = 1.0;
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return CreateFixtureResult(
                    fixture,
                    (0.4 * parseScore),
                    true,
                    0,
                    expectedStrings.Count + expectedArrays.Count,
                    0.0,
                    ["Candidate output parsed as JSON but did not produce an object root."]);
            }

            var root = document.RootElement;
            var keysPresent = 0;
            var valueScores = new List<double>(expectedStrings.Count + expectedArrays.Count);

            foreach (var expectedString in expectedStrings)
            {
                if (TryGetString(root, expectedString.Key, out var actualValue))
                {
                    keysPresent++;
                    valueScores.Add(StringSimilarity(actualValue, expectedString.Value));
                }
                else
                {
                    valueScores.Add(0.0);
                }
            }

            foreach (var expectedArray in expectedArrays)
            {
                if (TryGetArray(root, expectedArray.Key, out var actualValues))
                {
                    keysPresent++;
                    valueScores.Add(ThemeOverlap(expectedArray.Value, actualValues));
                }
                else
                {
                    valueScores.Add(0.0);
                }
            }

            var totalKeys = expectedStrings.Count + expectedArrays.Count;
            var keyScore = totalKeys == 0 ? 1.0 : keysPresent / (double)totalKeys;
            var valueScore = valueScores.Count == 0 ? 1.0 : valueScores.Average();
            var weightedScore = Math.Min(1.0, (0.4 * parseScore) + (0.3 * keyScore) + (0.3 * valueScore));

            return CreateFixtureResult(fixture, weightedScore, true, keysPresent, totalKeys, valueScore, Array.Empty<string>());
        }
    }

    /// <summary>
    /// Creates the structured benchmark result for the current fixture.
    /// </summary>
    private static ModelBenchmarkFixtureResult CreateFixtureResult(
        ModelBenchmarkFixtureDefinition fixture,
        double totalScore,
        bool parseSucceeded,
        int keysPresent,
        int totalKeys,
        double valueScore,
        IReadOnlyList<string> notes)
    {
        var dimensions = new[]
        {
            new ModelBenchmarkDimensionScore(
                Dimension: "json-parsable",
                Score: parseSucceeded ? 1.0 : 0.0,
                Weight: 0.4,
                Passed: parseSucceeded,
                Detail: parseSucceeded ? "Output parsed as JSON." : "Output did not parse as JSON."),
            new ModelBenchmarkDimensionScore(
                Dimension: "required-keys",
                Score: totalKeys == 0 ? 1.0 : keysPresent / (double)totalKeys,
                Weight: 0.3,
                Passed: totalKeys == 0 || keysPresent == totalKeys,
                Detail: $"{keysPresent}/{totalKeys} required keys present."),
            new ModelBenchmarkDimensionScore(
                Dimension: "value-similarity",
                Score: valueScore,
                Weight: 0.3,
                Passed: valueScore >= 0.999,
                Detail: $"Expected-value similarity {valueScore:0.00}.")
        };

        return new ModelBenchmarkFixtureResult(
            FixtureId: fixture.FixtureId,
            PromptId: fixture.PromptId,
            DisplayName: fixture.DisplayName,
            Weight: fixture.Weight,
            Score: totalScore,
            Dimensions: dimensions,
            Notes: notes);
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetArray(JsonElement root, string name, out IReadOnlyList<string> value)
    {
        if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Array)
        {
            var items = new List<string>(element.GetArrayLength());
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        items.Add(text);
                    }
                }
            }

            value = items;
            return items.Count > 0;
        }

        value = Array.Empty<string>();
        return false;
    }

    private static double StringSimilarity(string actual, string expected)
    {
        var a = Normalize(actual);
        var b = Normalize(expected);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0.0;
        }

        if (a == b) return 1.0;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return 0.7;
        return 0.0;
    }

    private static double ThemeOverlap(IReadOnlyList<string> expectedThemes, IReadOnlyList<string> actualThemes)
    {
        if (expectedThemes.Count == 0) return 0.0;

        var actualSet = new HashSet<string>(actualThemes.Select(Normalize), StringComparer.Ordinal);
        var matched = 0;
        foreach (var expected in expectedThemes)
        {
            var normalizedExpected = Normalize(expected);
            if (actualSet.Contains(normalizedExpected)
                || actualSet.Any(value => value.Contains(normalizedExpected, StringComparison.Ordinal)
                    || normalizedExpected.Contains(value, StringComparison.Ordinal)))
            {
                matched++;
            }
        }

        return (double)matched / expectedThemes.Count;
    }

    private static string Normalize(string value)
        => value.Trim().ToLowerInvariant();
}
