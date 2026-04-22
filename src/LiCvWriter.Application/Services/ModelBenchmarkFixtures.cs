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

    /// <summary>
    /// Scores a candidate JSON response against the fixture's expected output.
    /// Returns a value in the range [0, 1].
    /// </summary>
    public static double Score(string? candidateJson)
    {
        if (string.IsNullOrWhiteSpace(candidateJson))
        {
            return 0.0;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(candidateJson);
        }
        catch (JsonException)
        {
            return 0.0;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return 0.4; // valid JSON but wrong shape — minimal credit for parsing
            }

            var root = document.RootElement;
            var keysPresent = 0;
            if (TryGetString(root, "roleTitle", out _)) keysPresent++;
            if (TryGetString(root, "companyName", out _)) keysPresent++;
            if (TryGetArray(root, "mustHaveThemes", out _)) keysPresent++;

            var keyScore = 0.3 * (keysPresent / 3.0);

            var roleScore = TryGetString(root, "roleTitle", out var actualRole)
                ? StringSimilarity(actualRole, ExpectedRoleTitle)
                : 0.0;
            var companyScore = TryGetString(root, "companyName", out var actualCompany)
                ? StringSimilarity(actualCompany, ExpectedCompanyName)
                : 0.0;
            var themeScore = TryGetArray(root, "mustHaveThemes", out var themes)
                ? ThemeOverlap(themes)
                : 0.0;

            // Value-similarity portion is worth 0.3 total: split evenly across the three fields.
            var valueScore = 0.3 * ((roleScore + companyScore + themeScore) / 3.0);

            return Math.Min(1.0, 0.4 + keyScore + valueScore);
        }
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

    private static double ThemeOverlap(IReadOnlyList<string> themes)
    {
        if (themes.Count == 0) return 0.0;

        var actualSet = new HashSet<string>(themes.Select(Normalize), StringComparer.Ordinal);
        var matched = 0;
        foreach (var expected in ExpectedThemes)
        {
            var normalizedExpected = Normalize(expected);
            if (actualSet.Contains(normalizedExpected)
                || actualSet.Any(value => value.Contains(normalizedExpected, StringComparison.Ordinal)
                    || normalizedExpected.Contains(value, StringComparison.Ordinal)))
            {
                matched++;
            }
        }

        return (double)matched / ExpectedThemes.Count;
    }

    private static string Normalize(string value)
        => value.Trim().ToLowerInvariant();
}
