using LiCvWriter.Application.Services;

namespace LiCvWriter.Tests.Application;

public sealed class ModelBenchmarkFixturesTests
{
    [Fact]
    public void Score_PerfectExtraction_NearOne()
    {
        var json = """
            {
              "roleTitle": "Senior Backend Engineer",
              "companyName": "Acme Robotics",
              "mustHaveThemes": ["go", "kubernetes", "distributed systems"]
            }
            """;

        var score = ModelBenchmarkFixtures.Score(json);

        Assert.InRange(score, 0.99, 1.0);
    }

    [Fact]
    public void Score_AllRequiredKeysButWrongValues_GetsParsePlusKeyCredit()
    {
        var json = """
            {
              "roleTitle": "Frontend Designer",
              "companyName": "Other Corp",
              "mustHaveThemes": ["html", "css", "figma"]
            }
            """;

        var score = ModelBenchmarkFixtures.Score(json);

        // 0.4 (parse) + 0.3 (all 3 keys present) + 0 (no value match) = 0.7
        Assert.InRange(score, 0.69, 0.71);
    }

    [Fact]
    public void Score_MissingRequiredKey_GetsPartialKeyCredit()
    {
        var json = """
            {
              "roleTitle": "Senior Backend Engineer",
              "companyName": "Acme Robotics"
            }
            """;

        var score = ModelBenchmarkFixtures.Score(json);

        // 0.4 (parse) + 0.2 (2/3 keys) + 0.3 * (1+1+0)/3 = 0.4 + 0.2 + 0.2 = 0.8
        Assert.InRange(score, 0.79, 0.81);
    }

    [Fact]
    public void Score_MalformedJson_IsZero()
    {
        var score = ModelBenchmarkFixtures.Score("{ this is not json");

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Score_EmptyOrWhitespace_IsZero()
    {
        Assert.Equal(0.0, ModelBenchmarkFixtures.Score(null));
        Assert.Equal(0.0, ModelBenchmarkFixtures.Score(""));
        Assert.Equal(0.0, ModelBenchmarkFixtures.Score("   "));
    }

    [Fact]
    public void Score_JsonArrayInsteadOfObject_GivesMinimalParseCredit()
    {
        var score = ModelBenchmarkFixtures.Score("""["a", "b"]""");

        Assert.Equal(0.4, score);
    }

    [Fact]
    public void Score_PartialThemeOverlap_ScalesValueScoreProportionally()
    {
        var json = """
            {
              "roleTitle": "Senior Backend Engineer",
              "companyName": "Acme Robotics",
              "mustHaveThemes": ["go"]
            }
            """;

        var score = ModelBenchmarkFixtures.Score(json);

        // 0.4 + 0.3 (all keys) + 0.3 * (1 + 1 + 1/3)/3 = 0.4 + 0.3 + 0.233 ≈ 0.933
        Assert.InRange(score, 0.92, 0.94);
    }
}
