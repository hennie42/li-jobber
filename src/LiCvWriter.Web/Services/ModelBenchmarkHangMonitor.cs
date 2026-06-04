using LiCvWriter.Application.Models;

namespace LiCvWriter.Web.Services;

/// <summary>
/// Tracks per-model benchmark progress and cancels a model slot when the hang grace period expires.
/// </summary>
public sealed class ModelBenchmarkHangMonitor(
    ModelBenchmarkHangClockPolicy policy,
    TimeProvider timeProvider,
    OperationStatusService operations)
{
    public ModelHangClockState CreateClock()
        => new(policy, timeProvider.GetUtcNow());

    public async Task MonitorAsync(
        string model,
        IReadOnlyList<ModelBenchmarkResult> partialResults,
        ModelHangClockState hangClock,
        CancellationTokenSource hangTerminationCts,
        Action<string, IReadOnlyList<ModelBenchmarkResult>, ModelHangClockState, DateTimeOffset> updateHangState,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(policy.PollInterval, timeProvider);

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var now = timeProvider.GetUtcNow();
            if (hangClock.TryEnterWarning(now))
            {
                updateHangState(model, partialResults, hangClock, now);
                operations.Info($"Monitoring suspected benchmark hang: {model}", hangClock.HangDetail);
            }

            if (!hangClock.ShouldTerminate(now))
            {
                continue;
            }

            updateHangState(model, partialResults, hangClock, now);
            hangTerminationCts.Cancel();
            break;
        }
    }
}

public sealed class ModelHangClockState
{
    private readonly object gate = new();
    private readonly ModelBenchmarkHangClockPolicy policy;
    private BenchmarkProgressSignature? lastSignature;
    private ModelBenchmarkRunPhase currentPhase;
    private string? currentFixtureDisplayName;
    private int currentFixtureNumber;

    public ModelHangClockState(ModelBenchmarkHangClockPolicy policy, DateTimeOffset startedAt)
    {
        this.policy = policy;
        currentPhase = ModelBenchmarkRunPhase.Preparing;
        LastRealProgressUtc = startedAt;
    }

    public DateTimeOffset LastRealProgressUtc { get; private set; }

    public ModelBenchmarkHangState HangState { get; private set; }

    public string? HangDetail { get; private set; }

    public DateTimeOffset? WarningStartedUtc { get; private set; }

    public DateTimeOffset? DeadlineUtc { get; private set; }

    public void RecordRealProgress(ModelBenchmarkProgress progress, DateTimeOffset now)
    {
        lock (gate)
        {
            var nextSignature = new BenchmarkProgressSignature(
                progress.Phase,
                progress.CompletedFixtureCount,
                progress.CurrentFixtureNumber,
                progress.CurrentFixtureId,
                progress.Detail);

            if (lastSignature == nextSignature)
            {
                return;
            }

            lastSignature = nextSignature;
            currentPhase = progress.Phase;
            currentFixtureDisplayName = progress.CurrentFixtureDisplayName;
            currentFixtureNumber = progress.CurrentFixtureNumber;
            LastRealProgressUtc = now;
            HangState = ModelBenchmarkHangState.None;
            HangDetail = null;
            WarningStartedUtc = null;
            DeadlineUtc = null;
        }
    }

    public bool TryEnterWarning(DateTimeOffset now)
    {
        lock (gate)
        {
            var warningAfter = policy.GetWarningAfter(currentPhase);
            if (HangState == ModelBenchmarkHangState.Warning || (now - LastRealProgressUtc) < warningAfter)
            {
                return false;
            }

            HangState = ModelBenchmarkHangState.Warning;
            WarningStartedUtc = now;
            DeadlineUtc = now + policy.GetGracePeriod(currentPhase);
            HangDetail = BuildHangDetail(now, prefix: "No benchmark progress detected");
            return true;
        }
    }

    public bool ShouldTerminate(DateTimeOffset now)
    {
        lock (gate)
        {
            return HangState == ModelBenchmarkHangState.Warning
                && DeadlineUtc is { } deadline
                && now >= deadline;
        }
    }

    public string BuildFailureReason(DateTimeOffset now)
    {
        lock (gate)
        {
            return BuildHangDetail(now, prefix: "Benchmark hang detected");
        }
    }

    private string BuildHangDetail(DateTimeOffset now, string prefix)
    {
        var phaseLabel = currentPhase switch
        {
            ModelBenchmarkRunPhase.Preparing => "while preparing the current model",
            ModelBenchmarkRunPhase.Warmup => "during warm-up",
            ModelBenchmarkRunPhase.Evaluating => currentFixtureNumber > 0 && !string.IsNullOrWhiteSpace(currentFixtureDisplayName)
                ? $"during fixture {currentFixtureNumber}: {currentFixtureDisplayName}"
                : "during evaluation",
            ModelBenchmarkRunPhase.Cleanup => "during cleanup",
            ModelBenchmarkRunPhase.Finalizing => "during finalization",
            _ => "during the current benchmark phase"
        };

        return $"{prefix} for {FormatInactivity(now - LastRealProgressUtc)} {phaseLabel}. If the stall continues, the queue will move on.";
    }

    private static string FormatInactivity(TimeSpan inactivity)
        => inactivity.TotalMinutes >= 1
            ? $"{Math.Ceiling(inactivity.TotalMinutes):0} minute(s)"
            : $"{Math.Ceiling(Math.Max(inactivity.TotalSeconds, 1)):0} second(s)";
}

internal sealed record BenchmarkProgressSignature(
    ModelBenchmarkRunPhase Phase,
    int CompletedFixtureCount,
    int CurrentFixtureNumber,
    string? CurrentFixtureId,
    string Detail);