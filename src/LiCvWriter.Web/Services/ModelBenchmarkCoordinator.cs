using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LiCvWriter.Web.Services;

/// <summary>
/// Drives a sequential benchmark across an arbitrary list of installed Ollama
/// models, persists the ranked outcome through <see cref="WorkspaceSession"/>,
/// and surfaces live progress to the UI via <see cref="Changed"/>. A single
/// run is active at a time; <see cref="Cancel"/> signals the in-flight model
/// to stop and short-circuits the remaining queue.
/// </summary>
public sealed class ModelBenchmarkCoordinator(
    IServiceScopeFactory scopeFactory,
    WorkspaceSession workspace,
    OllamaOptions ollamaOptions,
    TimeProvider timeProvider)
{
    private readonly object gate = new();
    private ModelBenchmarkSession? current;
    private CancellationTokenSource? activeCts;

    public event Action? Changed;

    public ModelBenchmarkSession? Current
    {
        get { lock (gate) { return current; } }
    }

    public ModelBenchmarkSession? Last => workspace.LastBenchmarkSession;

    public bool IsRunning => Current is { IsRunning: true };

    public Task StartAsync(IReadOnlyList<string> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        var trimmed = models
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("At least one installed model is required to start a benchmark.");
        }

        CancellationToken token;
        lock (gate)
        {
            if (current is { IsRunning: true })
            {
                throw new InvalidOperationException("A benchmark run is already in progress.");
            }

            activeCts = new CancellationTokenSource();
            token = activeCts.Token;
            current = new ModelBenchmarkSession(
                StartedUtc: timeProvider.GetUtcNow(),
                CompletedUtc: null,
                IsRunning: true,
                IsCancelled: false,
                CompletedCount: 0,
                TotalCount: trimmed.Length,
                CurrentModel: trimmed[0],
                Results: Array.Empty<ModelBenchmarkResult>());
        }

        Changed?.Invoke();
        return RunAsync(trimmed, token);
    }

    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (gate)
        {
            cts = activeCts;
        }

        cts?.Cancel();
    }

    private async Task RunAsync(IReadOnlyList<string> models, CancellationToken cancellationToken)
    {
        var results = new List<ModelBenchmarkResult>(models.Count);
        var perModelTimeoutSeconds = ollamaOptions.MaxOperationSeconds;
        var cancelled = false;

        for (var index = 0; index < models.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var model = models[index];
            UpdateProgress(model, results.ToArray(), index);

            ModelBenchmarkResult result;
            using var perModelCts = perModelTimeoutSeconds > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;

            if (perModelCts is not null)
            {
                perModelCts.CancelAfter(TimeSpan.FromSeconds(perModelTimeoutSeconds));
            }

            var effectiveToken = perModelCts?.Token ?? cancellationToken;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var benchmarkService = scope.ServiceProvider.GetRequiredService<OllamaModelBenchmarkService>();
                result = await benchmarkService.RunSingleAsync(model, effectiveToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            catch (OperationCanceledException)
            {
                // Per-model timeout — record as a failure and continue the queue.
                result = new ModelBenchmarkResult(
                    Model: model,
                    Rank: 0,
                    OverallScore: 0.0,
                    QualityScore: 0.0,
                    DecodeTokensPerSecond: null,
                    LoadDuration: null,
                    TotalDuration: null,
                    Fit: OllamaCapacityFit.Unknown,
                    Notes: Array.Empty<string>(),
                    FailedReason: $"Timed out after {perModelTimeoutSeconds}s.");
            }
            catch (Exception exception)
            {
                result = new ModelBenchmarkResult(
                    Model: model,
                    Rank: 0,
                    OverallScore: 0.0,
                    QualityScore: 0.0,
                    DecodeTokensPerSecond: null,
                    LoadDuration: null,
                    TotalDuration: null,
                    Fit: OllamaCapacityFit.Unknown,
                    Notes: Array.Empty<string>(),
                    FailedReason: exception.Message);
            }

            results.Add(result);
            UpdateProgress(
                index + 1 < models.Count ? models[index + 1] : null,
                results.ToArray(),
                index + 1);
        }

        var ranked = RankResults(results);
        var session = new ModelBenchmarkSession(
            StartedUtc: current?.StartedUtc ?? timeProvider.GetUtcNow(),
            CompletedUtc: timeProvider.GetUtcNow(),
            IsRunning: false,
            IsCancelled: cancelled,
            CompletedCount: ranked.Count,
            TotalCount: models.Count,
            CurrentModel: null,
            Results: ranked);

        lock (gate)
        {
            current = session;
            activeCts?.Dispose();
            activeCts = null;
        }

        workspace.SetLastBenchmarkSession(session);
        Changed?.Invoke();
    }

    private void UpdateProgress(string? currentModel, IReadOnlyList<ModelBenchmarkResult> partialResults, int completedCount)
    {
        lock (gate)
        {
            if (current is null)
            {
                return;
            }

            current = current with
            {
                CurrentModel = currentModel,
                CompletedCount = completedCount,
                Results = partialResults
            };
        }

        Changed?.Invoke();
    }

    private static IReadOnlyList<ModelBenchmarkResult> RankResults(IEnumerable<ModelBenchmarkResult> results)
    {
        var ordered = results
            .OrderByDescending(static result => result.Succeeded)
            .ThenByDescending(static result => result.OverallScore)
            .ThenByDescending(static result => result.QualityScore)
            .ToArray();

        for (var index = 0; index < ordered.Length; index++)
        {
            ordered[index] = ordered[index] with { Rank = index + 1 };
        }

        return ordered;
    }
}
