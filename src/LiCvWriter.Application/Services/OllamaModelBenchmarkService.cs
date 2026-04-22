using System.Diagnostics;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;

namespace LiCvWriter.Application.Services;

/// <summary>
/// Runs the standard per-model benchmark: a capacity probe (warm-up call →
/// decode tok/s + GPU fit) followed by a deterministic JSON-extraction quality
/// task scored by <see cref="ModelBenchmarkFixtures"/>. Combines both signals
/// into a single overall score for ranking. Never throws; failures surface
/// as a <see cref="ModelBenchmarkResult"/> with <c>FailedReason</c> set.
/// </summary>
public sealed class OllamaModelBenchmarkService(
    OllamaCapacityProbe capacityProbe,
    ILlmClient llmClient,
    OllamaOptions options)
{
    // Decode speed normalization ceiling — anything at or above this maps to 1.0
    // when computing the speed half of the overall score.
    private const double SpeedNormalizationCeilingTokensPerSecond = 60.0;

    private const double QualityWeight = 0.6;
    private const double SpeedWeight = 0.4;

    public async Task<ModelBenchmarkResult> RunSingleAsync(string model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return Failed(model ?? string.Empty, "Empty model name.");
        }

        var stopwatch = Stopwatch.StartNew();
        OllamaCapacityVerdict verdict;
        try
        {
            verdict = await capacityProbe.ProbeAsync(model, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed(model, $"Capacity probe failed: {exception.Message}", stopwatch.Elapsed);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var jsonInvoker = new LlmJsonInvoker(llmClient);
        var qualityRequest = new LlmRequest(
            Model: model,
            SystemPrompt: ModelBenchmarkFixtures.SystemPrompt,
            Messages: [new LlmChatMessage("user", ModelBenchmarkFixtures.UserPrompt)],
            UseChatEndpoint: options.UseChatEndpoint,
            Stream: false,
            Think: "low",
            KeepAlive: options.KeepAlive,
            Temperature: 0.0,
            ResponseFormat: LlmResponseFormat.Json);

        double qualityScore;
        try
        {
            var invocation = await jsonInvoker.InvokeAsync(
                qualityRequest,
                parse: static content => content,
                progress: null,
                cancellationToken);

            // Score the strict-stage content first, then fall back to the most
            // recoverable attempt (typically the lenient or repaired output).
            qualityScore = invocation.Attempts
                .Select(attempt => ModelBenchmarkFixtures.Score(attempt.RawContent))
                .DefaultIfEmpty(0.0)
                .Max();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed(model, $"Quality call failed: {exception.Message}", stopwatch.Elapsed, verdict);
        }

        stopwatch.Stop();

        var speedScore = NormalizeSpeed(verdict.DecodeTokensPerSecond);
        var overall = Math.Clamp((QualityWeight * qualityScore) + (SpeedWeight * speedScore), 0.0, 1.0);

        return new ModelBenchmarkResult(
            Model: model,
            Rank: 0,
            OverallScore: overall,
            QualityScore: qualityScore,
            DecodeTokensPerSecond: verdict.DecodeTokensPerSecond,
            LoadDuration: verdict.LoadDuration,
            TotalDuration: stopwatch.Elapsed,
            Fit: verdict.Fit,
            Notes: verdict.Notes,
            FailedReason: null);
    }

    private static double NormalizeSpeed(double? decodeTokensPerSecond)
    {
        if (decodeTokensPerSecond is null or <= 0)
        {
            return 0.0;
        }

        return Math.Clamp(decodeTokensPerSecond.Value / SpeedNormalizationCeilingTokensPerSecond, 0.0, 1.0);
    }

    private static ModelBenchmarkResult Failed(
        string model,
        string reason,
        TimeSpan? totalDuration = null,
        OllamaCapacityVerdict? verdict = null)
        => new(
            Model: model,
            Rank: 0,
            OverallScore: 0.0,
            QualityScore: 0.0,
            DecodeTokensPerSecond: verdict?.DecodeTokensPerSecond,
            LoadDuration: verdict?.LoadDuration,
            TotalDuration: totalDuration,
            Fit: verdict?.Fit ?? OllamaCapacityFit.Unknown,
            Notes: verdict?.Notes ?? Array.Empty<string>(),
            FailedReason: reason);
}
