using LiCvWriter.Application.Services;

namespace LiCvWriter.Tests.Application;

public sealed class FoundryTextBenchmarkSuitabilityEvaluatorTests
{
    [Fact]
    public void Evaluate_WhisperAlias_ReturnsAudioConstraint()
    {
        var assessment = FoundryTextBenchmarkSuitabilityEvaluator.Evaluate(
            "whisper-small",
            "Whisper Small",
            "OpenAI speech transcription model.");

        Assert.False(assessment.IsUsable);
        Assert.Equal("Audio transcription model; text JSON benchmark is not a good fit.", assessment.Reason);
    }

    [Fact]
    public void Evaluate_EmbeddingMetadata_ReturnsEmbeddingConstraint()
    {
        var assessment = FoundryTextBenchmarkSuitabilityEvaluator.Evaluate(
            "text-embed-large",
            "Text Embed Large",
            "Embedding model for semantic retrieval.",
            ["embedding"]);

        Assert.False(assessment.IsUsable);
        Assert.Equal("Embedding model; text JSON benchmark is not a good fit.", assessment.Reason);
    }

    [Fact]
    public void Evaluate_InstructTextModel_ReturnsUsable()
    {
        var assessment = FoundryTextBenchmarkSuitabilityEvaluator.Evaluate(
            "phi-3.5-mini",
            "Phi 3.5 Mini Instruct",
            "Compact instruct model for chat completions.");

        Assert.True(assessment.IsUsable);
        Assert.Null(assessment.Reason);
    }
}