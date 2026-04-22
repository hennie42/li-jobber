using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;

namespace LiCvWriter.Application.Services;

/// <summary>
/// Runs a small warm-up call against an installed Ollama model and combines the resulting
/// timing data with metadata from <c>/api/show</c> and <c>/api/ps</c> to classify how well
/// the model fits the host's hardware. Used by the Setup UI to surface "Comfortable",
/// "Partial offload" or "Too large for interactive use" verdicts before the user starts
/// real LLM-backed work.
/// </summary>
public sealed class OllamaCapacityProbe(ILlmClient llmClient, OllamaOptions options)
{
    private const string WarmupPrompt = "Respond with the single word: ready";

    public async Task<OllamaCapacityVerdict> ProbeAsync(string model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return OllamaCapacityVerdict.Unknown(model ?? string.Empty, "No model selected.");
        }

        OllamaModelInfo? modelInfo = null;
        try
        {
            modelInfo = await llmClient.GetModelInfoAsync(model, cancellationToken);
        }
        catch
        {
            // Best-effort metadata; the warm-up call is the authoritative signal.
        }

        LlmResponse warmup;
        try
        {
            warmup = await llmClient.GenerateAsync(
                new LlmRequest(
                    model,
                    SystemPrompt: null,
                    Messages: [new LlmChatMessage("user", WarmupPrompt)],
                    UseChatEndpoint: options.UseChatEndpoint,
                    Stream: false,
                    Think: "low",
                    KeepAlive: options.KeepAlive,
                    Temperature: 0.0,
                    NumPredict: options.CapacityWarmupNumPredict > 0 ? options.CapacityWarmupNumPredict : 64),
                progress: null,
                cancellationToken);
        }
        catch (Exception exception)
        {
            return OllamaCapacityVerdict.Unknown(model, $"Warm-up call failed: {exception.Message}");
        }

        OllamaModelAvailability availability;
        try
        {
            availability = await llmClient.VerifyModelAvailabilityAsync(cancellationToken);
        }
        catch
        {
            availability = new OllamaModelAvailability(string.Empty, model, true, Array.Empty<string>(), Array.Empty<OllamaRunningModel>());
        }

        var running = availability.EffectiveRunningModels
            .FirstOrDefault(runningModel => availability.IsModelLoaded(model)
                && (runningModel.Name.Equals(model, StringComparison.OrdinalIgnoreCase)
                    || runningModel.Model.Equals(model, StringComparison.OrdinalIgnoreCase)));

        return BuildVerdict(model, warmup, running, modelInfo, options);
    }

    internal static OllamaCapacityVerdict BuildVerdict(
        string model,
        LlmResponse warmup,
        OllamaRunningModel? running,
        OllamaModelInfo? modelInfo,
        OllamaOptions options)
    {
        var notes = new List<string>();

        var decodeTokensPerSecond = ComputeTokensPerSecond(warmup.CompletionTokens, warmup.EvalDuration);
        var promptTokensPerSecond = ComputeTokensPerSecond(warmup.PromptTokens, warmup.PromptEvalDuration);

        long? vramBytes = running?.SizeVramBytes;
        long? residentSize = ReadResidentSize(running);
        double? gpuOffload = (vramBytes is > 0 && residentSize is > 0)
            ? (double)vramBytes.Value / residentSize.Value
            : null;

        var fit = ClassifyFit(decodeTokensPerSecond, gpuOffload, vramBytes, residentSize, options, notes, warmup);

        if (modelInfo?.ParameterSize is { Length: > 0 } parameterSize)
        {
            notes.Add($"Parameter size: {parameterSize}{(modelInfo.QuantizationLevel is null ? string.Empty : $" ({modelInfo.QuantizationLevel})")}.");
        }

        if (modelInfo?.ContextLength is > 0)
        {
            notes.Add($"Model context length: {modelInfo.ContextLength:N0} tokens.");
        }

        if (warmup.LoadDuration is { } load && load > TimeSpan.FromSeconds(5))
        {
            notes.Add($"Cold load took {load.TotalSeconds:0.0}s — the model may be evicted between calls due to RAM/VRAM pressure.");
        }

        var headline = BuildHeadline(fit, decodeTokensPerSecond);
        return new OllamaCapacityVerdict(
            model,
            fit,
            headline,
            notes,
            decodeTokensPerSecond,
            promptTokensPerSecond,
            warmup.LoadDuration,
            vramBytes,
            residentSize,
            gpuOffload,
            modelInfo,
            DateTimeOffset.UtcNow);
    }

    private static OllamaCapacityFit ClassifyFit(
        double? decodeTokensPerSecond,
        double? gpuOffload,
        long? vramBytes,
        long? residentSize,
        OllamaOptions options,
        List<string> notes,
        LlmResponse warmup)
    {
        // Static signal: GPU offload ratio.
        if (vramBytes is 0 && residentSize is > 0)
        {
            notes.Add("Model is running entirely on CPU (no VRAM allocation reported).");
        }
        else if (gpuOffload is < 1.0 and > 0)
        {
            notes.Add($"Only {gpuOffload.Value * 100:0}% of the model is on GPU; the rest spilled to CPU and will be much slower.");
        }
        else if (gpuOffload is >= 1.0)
        {
            notes.Add("Model fits entirely in GPU memory.");
        }

        // Dynamic signal: decode tok/s.
        if (decodeTokensPerSecond is null)
        {
            notes.Add("Could not measure decode speed; verify Ollama returned eval_count and eval_duration.");
            return OllamaCapacityFit.Unknown;
        }

        notes.Add($"Decode speed: {decodeTokensPerSecond.Value:0.0} tokens/s.");

        if (decodeTokensPerSecond.Value < options.CapacityTooSlowTokensPerSecond)
        {
            return vramBytes is 0 && residentSize is > 0
                ? OllamaCapacityFit.CpuOnly
                : OllamaCapacityFit.TooLargeForInteractive;
        }

        if (gpuOffload is < 1.0 and > 0)
        {
            return OllamaCapacityFit.PartialOffload;
        }

        if (vramBytes is 0 && residentSize is > 0)
        {
            return OllamaCapacityFit.CpuOnly;
        }

        return decodeTokensPerSecond.Value >= options.CapacityComfortableTokensPerSecond
            ? OllamaCapacityFit.Comfortable
            : OllamaCapacityFit.Usable;
    }

    private static long? ReadResidentSize(OllamaRunningModel? running)
    {
        // Prefer the total resident size from /api/ps. Falls back to size_vram which
        // at least bounds the comparison to "fully on GPU" when the field is missing.
        return running?.SizeBytes ?? running?.SizeVramBytes;
    }

    private static double? ComputeTokensPerSecond(long? tokens, TimeSpan? duration)
    {
        if (tokens is null or <= 0 || duration is null || duration.Value <= TimeSpan.Zero)
        {
            return null;
        }

        return tokens.Value / duration.Value.TotalSeconds;
    }

    private static string BuildHeadline(OllamaCapacityFit fit, double? decodeTokensPerSecond)
    {
        var speed = decodeTokensPerSecond is null ? string.Empty : $" ({decodeTokensPerSecond.Value:0.0} tok/s)";
        return fit switch
        {
            OllamaCapacityFit.Comfortable => $"Comfortable on this hardware{speed}.",
            OllamaCapacityFit.Usable => $"Usable, but not fast{speed}. Generation will feel slow on long drafts.",
            OllamaCapacityFit.PartialOffload => $"Partial GPU offload{speed}. Pick a smaller quantization for full GPU residency.",
            OllamaCapacityFit.CpuOnly => $"CPU-only inference{speed}. Workable for small calls; long drafts will take minutes.",
            OllamaCapacityFit.TooLargeForInteractive => $"Too large for interactive use on this hardware{speed}. Try a smaller parameter size or quantization.",
            _ => "Capacity unknown."
        };
    }
}
