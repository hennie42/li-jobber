using LiCvWriter.Application.Models;

namespace LiCvWriter.Web.Services;

public sealed class OperationStatusService
{
    private readonly object gate = new();
    private readonly List<OperationStatusEntry> entries = [];
    private int busyDepth;
    private DateTimeOffset? busyStartedAt;
    private string currentMessage = "Idle";
    private string? currentDetail;
    private LlmOperationTelemetry? currentLlmTelemetry;
    private LlmOperationTelemetry? lastCompletedLlmTelemetry;

    public event Action? Changed;

    public bool IsBusy => Read(() => busyDepth > 0);

    public DateTimeOffset? BusyStartedAt => Read(() => busyStartedAt);

    public string CurrentMessage => Read(() => currentMessage);

    public string? CurrentDetail => Read(() => currentDetail);

    public LlmOperationTelemetry? CurrentLlmTelemetry => Read(() => currentLlmTelemetry);

    public LlmOperationTelemetry? ActiveLlmTelemetry
        => Read(() => currentLlmTelemetry is { Completed: false } telemetry ? telemetry : null);

    public LlmOperationTelemetry? LastCompletedLlmTelemetry => Read(() => lastCompletedLlmTelemetry);

    public IReadOnlyList<OperationStatusEntry> Entries => Read(() => entries.ToArray());

    public async Task RunAsync(string message, string? detail, Func<Task> action)
    {
        Begin(message, detail);
        var started = DateTimeOffset.Now;

        try
        {
            await action();
            Success($"Completed: {message}", detail, DateTimeOffset.Now - started);
        }
        catch (Exception exception)
        {
            Error($"Failed: {message}", exception.Message, DateTimeOffset.Now - started);
            throw;
        }
        finally
        {
            End();
        }
    }

    public async Task<T> RunAsync<T>(string message, string? detail, Func<Task<T>> action)
    {
        Begin(message, detail);
        var started = DateTimeOffset.Now;

        try
        {
            var result = await action();
            Success($"Completed: {message}", detail, DateTimeOffset.Now - started);
            return result;
        }
        catch (Exception exception)
        {
            Error($"Failed: {message}", exception.Message, DateTimeOffset.Now - started);
            throw;
        }
        finally
        {
            End();
        }
    }

    public void Info(string message, string? detail = null)
    {
        lock (gate)
        {
            ResetCurrentLlmTelemetryUnsafe();
            currentMessage = message;
            currentDetail = detail;
            AddEntryUnsafe("info", message, detail);
        }

        NotifyChanged();
    }

    public void Success(string message, string? detail = null, TimeSpan? duration = null)
    {
        lock (gate)
        {
            ResetCurrentLlmTelemetryUnsafe();
            currentMessage = message;
            currentDetail = detail;
            AddEntryUnsafe("success", message, detail, duration);
        }

        NotifyChanged();
    }

    public void Error(string message, string? detail = null, TimeSpan? duration = null)
    {
        lock (gate)
        {
            ResetCurrentLlmTelemetryUnsafe();
            currentMessage = message;
            currentDetail = detail;
            AddEntryUnsafe("error", message, detail, duration);
        }

        NotifyChanged();
    }

    public void UpdateCurrent(string message, string? detail = null)
    {
        lock (gate)
        {
            ResetCurrentLlmTelemetryUnsafe();
            currentMessage = message;
            currentDetail = detail;
        }

        NotifyChanged();
    }

    public void BeginLlmOperation(string message, string? detail = null)
    {
        lock (gate)
        {
            ResetAllLlmTelemetryUnsafe();
            currentMessage = message;
            currentDetail = detail;
        }

        NotifyChanged();
    }

    public void UpdateCurrent(LlmProgressUpdate update)
    {
        lock (gate)
        {
            currentMessage = update.Message;
            currentDetail = update.Detail;
            currentLlmTelemetry = new LlmOperationTelemetry(
                DateTimeOffset.Now,
                update.Message,
                update.Detail,
                update.Model,
                update.Elapsed,
                update.PromptTokens,
                update.CompletionTokens,
                update.EstimatedRemaining,
                update.ThinkingPreview,
                update.Completed,
                update.ResponseContent,
                update.ThinkingContent,
                update.Sequence);

            if (update.Completed)
            {
                // Promote to lastCompleted and clear current so that the monitor
                // transitions to "Last capture" between multi-step sub-calls rather
                // than keeping the first sub-call's thinking visible as "Live feed"
                // while the second sub-call has not yet produced any thinking output.
                lastCompletedLlmTelemetry = currentLlmTelemetry;
                currentLlmTelemetry = null;
            }
        }

        NotifyChanged();
    }

    private void Begin(string message, string? detail)
    {
        lock (gate)
        {
            ResetCurrentLlmTelemetryUnsafe();
            busyDepth++;
            busyStartedAt ??= DateTimeOffset.Now;
            currentMessage = message;
            currentDetail = detail;
            AddEntryUnsafe("busy", message, detail);
        }

        NotifyChanged();
    }

    private void End()
    {
        lock (gate)
        {
            busyDepth = Math.Max(0, busyDepth - 1);
            if (busyDepth == 0)
            {
                busyStartedAt = null;
            }

            if (busyDepth == 0 && currentMessage.StartsWith("Completed:", StringComparison.Ordinal))
            {
                currentDetail ??= "Ready for the next step.";
            }
        }

        NotifyChanged();
    }

    private void ResetCurrentLlmTelemetryUnsafe() => currentLlmTelemetry = null;

    private void ResetAllLlmTelemetryUnsafe()
    {
        currentLlmTelemetry = null;
        lastCompletedLlmTelemetry = null;
    }

    private void AddEntryUnsafe(string level, string message, string? detail, TimeSpan? duration = null)
    {
        entries.Insert(0, new OperationStatusEntry(DateTimeOffset.Now, level, message, detail, duration));

        if (entries.Count > 14)
        {
            entries.RemoveAt(entries.Count - 1);
        }
    }

    private T Read<T>(Func<T> read)
    {
        lock (gate)
        {
            return read();
        }
    }

    private void NotifyChanged() => Changed?.Invoke();
}

public sealed record OperationStatusEntry(DateTimeOffset Timestamp, string Level, string Message, string? Detail, TimeSpan? Duration = null);

public sealed record LlmOperationTelemetry(
    DateTimeOffset Timestamp,
    string Message,
    string? Detail,
    string Model,
    TimeSpan? Elapsed = null,
    long? PromptTokens = null,
    long? CompletionTokens = null,
    TimeSpan? EstimatedRemaining = null,
    string? ThinkingPreview = null,
    bool Completed = false,
    string? ResponseContent = null,
    string? ThinkingContent = null,
    long Sequence = 0)
{
    public bool HasTokenUsage => PromptTokens is not null || CompletionTokens is not null;

    public bool HasEstimatedRemaining => EstimatedRemaining is not null && EstimatedRemaining > TimeSpan.Zero;

    public bool HasThinkingPreview => !string.IsNullOrWhiteSpace(ThinkingPreview);

    public bool HasThinkingContent => !string.IsNullOrWhiteSpace(ThinkingContent);
}
