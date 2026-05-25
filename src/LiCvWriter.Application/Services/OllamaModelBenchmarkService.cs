using System.Diagnostics;
using System.Text.Json;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;

namespace LiCvWriter.Application.Services;

/// <summary>
/// Runs the standard per-model benchmark: a capacity probe (warm-up call →
/// decode tok/s + GPU fit) followed by a deterministic weighted fixture suite
/// scored by <see cref="ModelBenchmarkFixtures"/>. Combines both signals into a
/// single overall score for ranking. Never throws; failures surface
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

    public Task<ModelBenchmarkResult> RunSingleAsync(string model, CancellationToken cancellationToken = default)
        => RunSingleCoreAsync(model, progress: null, cancellationToken);

    public Task<ModelBenchmarkResult> RunSingleAsync(
        string model,
        Action<ModelBenchmarkProgress> progress,
        CancellationToken cancellationToken = default)
        => RunSingleCoreAsync(model, progress, cancellationToken);

    private async Task<ModelBenchmarkResult> RunSingleCoreAsync(
        string model,
        Action<ModelBenchmarkProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return Failed(model ?? string.Empty, "Empty model name.");
        }

        var totalFixtures = ModelBenchmarkFixtures.DefaultSuite.Count;
        var diagnostics = ModelBenchmarkDiagnostics.Empty;
        ReportProgress(progress, model, ModelBenchmarkRunPhase.Warmup, "Measuring load time and decode speed.", 0, totalFixtures);

        var stopwatch = Stopwatch.StartNew();
        OllamaCapacityVerdict verdict;
        var warmupStopwatch = Stopwatch.StartNew();
        try
        {
            verdict = await capacityProbe.ProbeAsync(
                model,
                progress is null ? null : update => ReportWarmupStreamingProgress(progress, model, totalFixtures, update, diagnostics),
                cancellationToken);
            warmupStopwatch.Stop();
            diagnostics = diagnostics with
            {
                WarmupDuration = warmupStopwatch.Elapsed,
                RuntimePathSummary = BuildRuntimePathSummary(verdict)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = ClassifyFailure(exception, "Capacity probe failed");
            return Failed(model, failure.Reason, stopwatch.Elapsed, notes: failure.Notes, diagnostics: diagnostics with { WarmupDuration = warmupStopwatch.Elapsed });
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

        IReadOnlyList<ModelBenchmarkFixtureResult> fixtureResults;
        double qualityScore;
        var evaluationStopwatch = Stopwatch.StartNew();
        try
        {
            var scoredFixtures = new List<ModelBenchmarkFixtureResult>(ModelBenchmarkFixtures.DefaultSuite.Count);
            foreach (var item in ModelBenchmarkFixtures.DefaultSuite.Select((fixture, index) => new { fixture, index }))
            {
                ReportProgress(
                    progress,
                    model,
                    ModelBenchmarkRunPhase.Evaluating,
                    $"Running fixture {item.index + 1} of {totalFixtures}: {item.fixture.DisplayName}.",
                    item.index,
                    totalFixtures,
                    item.index + 1,
                    item.fixture);

                var fixtureRequest = qualityRequest with
                {
                    SystemPrompt = item.fixture.SystemPrompt,
                    Messages = [new LlmChatMessage("user", item.fixture.UserPrompt)],
                    ResponseFormat = item.fixture.ResponseFormat,
                    PromptId = item.fixture.PromptId,
                    PromptVersion = LlmPromptCatalog.Version1
                };

                var invocation = await jsonInvoker.InvokeAsync(
                    fixtureRequest,
                    parse: static content => ValidateJsonCandidate(content),
                    progress: progress is null
                        ? null
                        : update => ReportFixtureStreamingProgress(progress, model, item.fixture, item.index, totalFixtures, update, diagnostics),
                    cancellationToken);

                var scoredFixture = invocation.Attempts
                    .Select(attempt => ModelBenchmarkFixtures.Evaluate(item.fixture, attempt.RawContent))
                    .OrderByDescending(static result => result.Score)
                    .FirstOrDefault()
                    ?? ModelBenchmarkFixtures.Evaluate(item.fixture, candidateJson: null);

                scoredFixtures.Add(scoredFixture);

                ReportProgress(
                    progress,
                    model,
                    ModelBenchmarkRunPhase.Evaluating,
                    $"Completed fixture {item.index + 1} of {totalFixtures}: {item.fixture.DisplayName}.",
                    item.index + 1,
                    totalFixtures,
                    item.index + 1,
                    item.fixture);
            }

            fixtureResults = scoredFixtures;
            var totalWeight = fixtureResults.Sum(static result => result.Weight);
            qualityScore = totalWeight <= 0.0
                ? 0.0
                : fixtureResults.Sum(static result => result.WeightedScore) / totalWeight;
            evaluationStopwatch.Stop();
            diagnostics = diagnostics with { EvaluationDuration = evaluationStopwatch.Elapsed };

            var finalizationStopwatch = Stopwatch.StartNew();
            ReportProgress(progress, model, ModelBenchmarkRunPhase.Finalizing, "Combining weighted fixture results.", totalFixtures, totalFixtures, diagnostics: diagnostics);
            finalizationStopwatch.Stop();
            diagnostics = diagnostics with { FinalizationDuration = finalizationStopwatch.Elapsed };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = ClassifyFailure(exception, "Quality call failed");
            return Failed(model, failure.Reason, stopwatch.Elapsed, verdict, failure.Notes, diagnostics);
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
            FailedReason: null,
            FixtureResults: fixtureResults)
        {
            Diagnostics = diagnostics
        };
    }

    private static void ReportWarmupStreamingProgress(
        Action<ModelBenchmarkProgress> progress,
        string model,
        int totalFixtures,
        LlmProgressUpdate update,
        ModelBenchmarkDiagnostics diagnostics)
    {
        var responseLength = update.ResponseContent?.Length ?? 0;
        var detail = update.Completed
            ? "Warm-up call completed; decode speed and load signals are being summarized."
            : $"Warm-up call is still active (stream update {Math.Max(update.Sequence, 1)}; {responseLength} response chars received).";

        progress(new ModelBenchmarkProgress(
            Model: model,
            Phase: ModelBenchmarkRunPhase.Warmup,
            Detail: detail,
            CompletedFixtureCount: 0,
            TotalFixtureCount: totalFixtures,
            LlmTelemetry: update,
            Diagnostics: diagnostics));
    }

    private static void ReportFixtureStreamingProgress(
        Action<ModelBenchmarkProgress> progress,
        string model,
        ModelBenchmarkFixtureDefinition fixture,
        int fixtureIndex,
        int totalFixtures,
        LlmProgressUpdate update,
        ModelBenchmarkDiagnostics diagnostics)
    {
        var responseLength = update.ResponseContent?.Length ?? 0;
        var thinkingLength = update.ThinkingContent?.Length ?? 0;
        var detail = update.Completed
            ? $"Structured output stream completed for fixture {fixtureIndex + 1} of {totalFixtures}: {fixture.DisplayName}."
            : $"Generating fixture {fixtureIndex + 1} of {totalFixtures}: {fixture.DisplayName} (stream update {Math.Max(update.Sequence, 1)}; response {responseLength} chars{(thinkingLength > 0 ? $", thinking {thinkingLength} chars" : string.Empty)}).";

        progress(new ModelBenchmarkProgress(
            Model: model,
            Phase: ModelBenchmarkRunPhase.Evaluating,
            Detail: detail,
            CompletedFixtureCount: fixtureIndex,
            TotalFixtureCount: totalFixtures,
            CurrentFixtureNumber: fixtureIndex + 1,
            CurrentFixtureId: fixture.FixtureId,
            CurrentFixtureDisplayName: fixture.DisplayName,
            CurrentPromptId: fixture.PromptId,
            LlmTelemetry: update,
            Diagnostics: diagnostics));
    }

    private static void ReportProgress(
        Action<ModelBenchmarkProgress>? progress,
        string model,
        ModelBenchmarkRunPhase phase,
        string detail,
        int completedFixtureCount,
        int totalFixtureCount,
        int currentFixtureNumber = 0,
        ModelBenchmarkFixtureDefinition? fixture = null,
        ModelBenchmarkDiagnostics? diagnostics = null)
    {
        progress?.Invoke(new ModelBenchmarkProgress(
            Model: model,
            Phase: phase,
            Detail: detail,
            CompletedFixtureCount: completedFixtureCount,
            TotalFixtureCount: totalFixtureCount,
            CurrentFixtureNumber: currentFixtureNumber,
            CurrentFixtureId: fixture?.FixtureId,
            CurrentFixtureDisplayName: fixture?.DisplayName,
            CurrentPromptId: fixture?.PromptId,
            Diagnostics: diagnostics));
    }

    private static double NormalizeSpeed(double? decodeTokensPerSecond)
    {
        if (decodeTokensPerSecond is null or <= 0)
        {
            return 0.0;
        }

        return Math.Clamp(decodeTokensPerSecond.Value / SpeedNormalizationCeilingTokensPerSecond, 0.0, 1.0);
    }

    private static string ValidateJsonCandidate(string content)
    {
        using var _ = JsonDocument.Parse(content);
        return content;
    }

    private static ModelBenchmarkResult Failed(
        string model,
        string reason,
        TimeSpan? totalDuration = null,
        OllamaCapacityVerdict? verdict = null,
        IReadOnlyList<string>? notes = null,
        ModelBenchmarkDiagnostics? diagnostics = null)
        => new(
            Model: model,
            Rank: 0,
            OverallScore: 0.0,
            QualityScore: 0.0,
            DecodeTokensPerSecond: verdict?.DecodeTokensPerSecond,
            LoadDuration: verdict?.LoadDuration,
            TotalDuration: totalDuration,
            Fit: verdict?.Fit ?? OllamaCapacityFit.Unknown,
            Notes: CombineNotes(verdict?.Notes, notes),
            FailedReason: reason)
        {
            Diagnostics = diagnostics ?? ModelBenchmarkDiagnostics.Empty
        };

    private static string BuildRuntimePathSummary(OllamaCapacityVerdict verdict)
        => verdict switch
        {
            { VramBytes: 0, ResidentSizeBytes: > 0 } => "Warm-up executed on CPU only.",
            { GpuOffloadRatio: >= 1.0 } => "Warm-up reported full GPU residency.",
            { GpuOffloadRatio: > 0 and < 1.0 } => $"Warm-up reported partial GPU residency ({verdict.GpuOffloadRatio.Value * 100:0}% on GPU).",
            _ => "Warm-up completed without residency telemetry from the runtime."
        };

    private static (string Reason, IReadOnlyList<string> Notes) ClassifyFailure(Exception exception, string fallbackPrefix)
        => exception is FoundryRuntimeException foundryException
            ? (foundryException.Message, foundryException.Notes)
            : ($"{fallbackPrefix}: {exception.Message}", Array.Empty<string>());

    private static IReadOnlyList<string> CombineNotes(IReadOnlyList<string>? primary, IReadOnlyList<string>? additional)
    {
        var combined = new List<string>();
        if (primary is { Count: > 0 })
        {
            combined.AddRange(primary.Where(static note => !string.IsNullOrWhiteSpace(note)));
        }

        if (additional is { Count: > 0 })
        {
            combined.AddRange(additional.Where(static note => !string.IsNullOrWhiteSpace(note)));
        }

        return combined.Count == 0
            ? Array.Empty<string>()
            : combined.Distinct(StringComparer.Ordinal).ToArray();
    }
}
