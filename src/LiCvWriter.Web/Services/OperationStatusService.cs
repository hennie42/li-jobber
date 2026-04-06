using LiCvWriter.Application.Models;

namespace LiCvWriter.Web.Services;

public sealed class OperationStatusService
{
    private readonly List<OperationStatusEntry> entries = [];
    private int busyDepth;

    public event Action? Changed;

    public bool IsBusy => busyDepth > 0;

    public string CurrentMessage { get; private set; } = "Idle";

    public string? CurrentDetail { get; private set; }

    public LlmOperationTelemetry? CurrentLlmTelemetry { get; private set; }

    public LlmOperationTelemetry? ActiveLlmTelemetry
        => CurrentLlmTelemetry is { Completed: false } telemetry ? telemetry : null;

    public LlmOperationTelemetry? LastCompletedLlmTelemetry { get; private set; }

    public IReadOnlyList<OperationStatusEntry> Entries => entries;

    public async Task RunAsync(string message, string? detail, Func<Task> action)
    {
        Begin(message, detail);

        try
        {
            await action();
            Success($"Completed: {message}", detail);
        }
        catch (Exception exception)
        {
            Error($"Failed: {message}", exception.Message);
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

        try
        {
            var result = await action();
            Success($"Completed: {message}", detail);
            return result;
        }
        catch (Exception exception)
        {
            Error($"Failed: {message}", exception.Message);
            throw;
        }
        finally
        {
            End();
        }
    }

    public void Info(string message, string? detail = null)
    {
        ResetCurrentLlmTelemetry();
        CurrentMessage = message;
        CurrentDetail = detail;
        AddEntry("info", message, detail);
    }

    public void Success(string message, string? detail = null)
    {
        ResetCurrentLlmTelemetry();
        CurrentMessage = message;
        CurrentDetail = detail;
        AddEntry("success", message, detail);
    }

    public void Error(string message, string? detail = null)
    {
        ResetCurrentLlmTelemetry();
        CurrentMessage = message;
        CurrentDetail = detail;
        AddEntry("error", message, detail);
    }

    public void UpdateCurrent(string message, string? detail = null)
    {
        ResetCurrentLlmTelemetry();
        CurrentMessage = message;
        CurrentDetail = detail;
        Changed?.Invoke();
    }

    public void UpdateCurrent(LlmProgressUpdate update)
    {
        CurrentMessage = update.Message;
        CurrentDetail = update.Detail;
        CurrentLlmTelemetry = new LlmOperationTelemetry(
            DateTimeOffset.Now,
            update.Message,
            update.Detail,
            update.Model,
            update.Elapsed,
            update.PromptTokens,
            update.CompletionTokens,
            update.EstimatedRemaining,
            update.ThinkingPreview,
            update.Completed);

        if (update.Completed)
        {
            LastCompletedLlmTelemetry = CurrentLlmTelemetry;
        }

        Changed?.Invoke();
    }

    private void Begin(string message, string? detail)
    {
        ResetCurrentLlmTelemetry();
        busyDepth++;
        CurrentMessage = message;
        CurrentDetail = detail;
        AddEntry("busy", message, detail);
    }

    private void End()
    {
        busyDepth = Math.Max(0, busyDepth - 1);
        if (!IsBusy && CurrentMessage.StartsWith("Completed:", StringComparison.Ordinal))
        {
            CurrentDetail ??= "Ready for the next step.";
        }

        Changed?.Invoke();
    }

    private void ResetCurrentLlmTelemetry() => CurrentLlmTelemetry = null;

    private void AddEntry(string level, string message, string? detail)
    {
        entries.Insert(0, new OperationStatusEntry(DateTimeOffset.Now, level, message, detail));

        if (entries.Count > 14)
        {
            entries.RemoveAt(entries.Count - 1);
        }

        Changed?.Invoke();
    }
}

public sealed record OperationStatusEntry(DateTimeOffset Timestamp, string Level, string Message, string? Detail);

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
    bool Completed = false)
{
    public bool HasTokenUsage => PromptTokens is not null || CompletionTokens is not null;

    public bool HasEstimatedRemaining => EstimatedRemaining is not null && EstimatedRemaining > TimeSpan.Zero;

    public bool HasThinkingPreview => !string.IsNullOrWhiteSpace(ThinkingPreview);
}
