using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;

namespace LiCvWriter.Tests.Application;

public sealed class OllamaCapacityProbeTests
{
    private static readonly OllamaOptions DefaultOptions = new()
    {
        CapacityTooSlowTokensPerSecond = 8.0,
        CapacityComfortableTokensPerSecond = 25.0
    };

    [Fact]
    public void BuildVerdict_FullGpuResidency_AndFastDecode_IsComfortable()
    {
        var warmup = CreateWarmup(evalTokens: 64, evalSeconds: 1.0); // 64 tok/s
        var running = new OllamaRunningModel("m:latest", "m:latest", null, SizeVramBytes: 8_000_000_000, SizeBytes: 8_000_000_000);

        var verdict = OllamaCapacityProbe.BuildVerdict("m:latest", warmup, running, ModelInfo("8B", "Q4_0", 8192), DefaultOptions);

        Assert.Equal(OllamaCapacityFit.Comfortable, verdict.Fit);
        Assert.NotNull(verdict.DecodeTokensPerSecond);
        Assert.InRange(verdict.DecodeTokensPerSecond!.Value, 63.0, 65.0);
        Assert.Equal(1.0, verdict.GpuOffloadRatio);
        Assert.Contains(verdict.Notes, n => n.Contains("Decode speed", StringComparison.Ordinal));
        Assert.Contains(verdict.Notes, n => n.Contains("Parameter size: 8B", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildVerdict_FullGpuResidency_AndModerateDecode_IsUsable()
    {
        var warmup = CreateWarmup(evalTokens: 64, evalSeconds: 4.0); // 16 tok/s
        var running = new OllamaRunningModel("m:latest", "m:latest", null, SizeVramBytes: 6_000_000_000, SizeBytes: 6_000_000_000);

        var verdict = OllamaCapacityProbe.BuildVerdict("m:latest", warmup, running, modelInfo: null, DefaultOptions);

        Assert.Equal(OllamaCapacityFit.Usable, verdict.Fit);
    }

    [Fact]
    public void BuildVerdict_PartialGpuOffload_FastEnough_IsPartialOffload()
    {
        var warmup = CreateWarmup(evalTokens: 64, evalSeconds: 4.0); // 16 tok/s — above too-slow threshold
        var running = new OllamaRunningModel("m:latest", "m:latest", null, SizeVramBytes: 4_000_000_000, SizeBytes: 8_000_000_000);

        var verdict = OllamaCapacityProbe.BuildVerdict("m:latest", warmup, running, modelInfo: null, DefaultOptions);

        Assert.Equal(OllamaCapacityFit.PartialOffload, verdict.Fit);
        Assert.NotNull(verdict.GpuOffloadRatio);
        Assert.InRange(verdict.GpuOffloadRatio!.Value, 0.49, 0.51);
        Assert.Contains(verdict.Notes, n => n.Contains("spilled to CPU", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildVerdict_NoVram_AndAcceptableDecode_IsCpuOnly()
    {
        var warmup = CreateWarmup(evalTokens: 64, evalSeconds: 4.0); // 16 tok/s
        var running = new OllamaRunningModel("m:latest", "m:latest", null, SizeVramBytes: 0, SizeBytes: 4_000_000_000);

        var verdict = OllamaCapacityProbe.BuildVerdict("m:latest", warmup, running, modelInfo: null, DefaultOptions);

        Assert.Equal(OllamaCapacityFit.CpuOnly, verdict.Fit);
        Assert.Contains(verdict.Notes, n => n.Contains("entirely on CPU", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildVerdict_NoVram_AndVerySlowDecode_IsCpuOnly()
    {
        var warmup = CreateWarmup(evalTokens: 16, evalSeconds: 8.0); // 2 tok/s — below too-slow threshold
        var running = new OllamaRunningModel("m:latest", "m:latest", null, SizeVramBytes: 0, SizeBytes: 12_000_000_000);

        var verdict = OllamaCapacityProbe.BuildVerdict("m:latest", warmup, running, modelInfo: null, DefaultOptions);

        Assert.Equal(OllamaCapacityFit.CpuOnly, verdict.Fit);
    }

    [Fact]
    public void BuildVerdict_PartialOffload_AndVerySlowDecode_IsTooLargeForInteractive()
    {
        var warmup = CreateWarmup(evalTokens: 16, evalSeconds: 8.0); // 2 tok/s
        var running = new OllamaRunningModel("m:latest", "m:latest", null, SizeVramBytes: 4_000_000_000, SizeBytes: 16_000_000_000);

        var verdict = OllamaCapacityProbe.BuildVerdict("m:latest", warmup, running, modelInfo: null, DefaultOptions);

        Assert.Equal(OllamaCapacityFit.TooLargeForInteractive, verdict.Fit);
        Assert.Contains("Too large", verdict.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildVerdict_MissingDecodeTiming_IsUnknown()
    {
        var warmup = CreateWarmup(evalTokens: null, evalSeconds: null);
        var running = new OllamaRunningModel("m:latest", "m:latest", null, SizeVramBytes: 4_000_000_000, SizeBytes: 4_000_000_000);

        var verdict = OllamaCapacityProbe.BuildVerdict("m:latest", warmup, running, modelInfo: null, DefaultOptions);

        Assert.Equal(OllamaCapacityFit.Unknown, verdict.Fit);
        Assert.Contains(verdict.Notes, n => n.Contains("Could not measure decode speed", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildVerdict_LongLoadDuration_AddsEvictionWarning()
    {
        var warmup = CreateWarmup(evalTokens: 64, evalSeconds: 1.0, loadSeconds: 20.0);
        var running = new OllamaRunningModel("m:latest", "m:latest", null, SizeVramBytes: 4_000_000_000, SizeBytes: 4_000_000_000);

        var verdict = OllamaCapacityProbe.BuildVerdict("m:latest", warmup, running, modelInfo: null, DefaultOptions);

        Assert.Contains(verdict.Notes, n => n.Contains("Cold load took", StringComparison.Ordinal));
    }

    private static LlmResponse CreateWarmup(long? evalTokens, double? evalSeconds, double? loadSeconds = null)
        => new(
            Model: "m:latest",
            Content: "ready",
            Thinking: null,
            Completed: true,
            PromptTokens: 4,
            CompletionTokens: evalTokens,
            Duration: evalSeconds is null ? null : TimeSpan.FromSeconds(evalSeconds.Value),
            LoadDuration: loadSeconds is null ? null : TimeSpan.FromSeconds(loadSeconds.Value),
            PromptEvalDuration: TimeSpan.FromSeconds(0.1),
            EvalDuration: evalSeconds is null ? null : TimeSpan.FromSeconds(evalSeconds.Value));

    private static OllamaModelInfo ModelInfo(string parameters, string quant, long contextLength)
        => new(Name: "m:latest", FileSizeBytes: 4_000_000_000, ParameterSize: parameters, QuantizationLevel: quant, Family: "llama", ContextLength: contextLength);
}
