using LiCvWriter.Application.Models;

namespace LiCvWriter.Application.Services;

/// <summary>
/// Classifies Foundry Local runtime exceptions into shorter user-facing failure
/// messages and recovery notes that fit the benchmark and setup UI.
/// </summary>
public static class FoundryRuntimeFailureClassifier
{
    private static readonly string[] TensorRtEngineLoadMarkers =
    [
        "NvTensorRTRTX EP failed to deserialize engine",
        "failed to deserialize engine"
    ];

    private static readonly string[] TensorRtOutOfMemoryMarkers =
    [
        "CUDA failure 2: out of memory",
        "out of memory ; GPU=",
        "cudaMalloc"
    ];

    private static readonly string[] ModelLoadMarkers =
    [
        "Error executing load_model",
        "Error loading model"
    ];

    private static readonly string[] ExecutionMarkers =
    [
        "Error executing chat_completions",
        "OnnxRuntimeGenAIException"
    ];

    public static bool IsRetriableModelLoadFailure(Exception exception)
        => Classify(exception) == FoundryRuntimeFailureKind.TensorRtEngineLoad;

    public static FoundryRuntimeException? TryCreateException(
        Exception exception,
        bool retryAttempted,
        string? modelCacheRoot = null,
        string? logsRoot = null,
        IReadOnlyList<string>? additionalNotes = null)
    {
        var failureKind = Classify(exception);
        if (failureKind == FoundryRuntimeFailureKind.Unknown)
        {
            return null;
        }

        var diagnosticDetail = GetTerminalMessage(exception);
        var notes = BuildNotes(failureKind, diagnosticDetail, modelCacheRoot, logsRoot, retryAttempted, additionalNotes);
        return new FoundryRuntimeException(
            failureKind,
            BuildMessage(failureKind, retryAttempted),
            retryAttempted,
            notes,
            exception);
    }

    private static FoundryRuntimeFailureKind Classify(Exception exception)
    {
        var messages = EnumerateMessages(exception);
        if (ContainsAny(messages, TensorRtOutOfMemoryMarkers))
        {
            return FoundryRuntimeFailureKind.TensorRtGpuOutOfMemory;
        }

        if (ContainsAny(messages, TensorRtEngineLoadMarkers))
        {
            return FoundryRuntimeFailureKind.TensorRtEngineLoad;
        }

        if (ContainsAny(messages, ModelLoadMarkers))
        {
            return FoundryRuntimeFailureKind.ModelLoad;
        }

        if (ContainsAny(messages, ExecutionMarkers))
        {
            return FoundryRuntimeFailureKind.Execution;
        }

        return FoundryRuntimeFailureKind.Unknown;
    }

    private static string BuildMessage(FoundryRuntimeFailureKind failureKind, bool retryAttempted)
        => failureKind switch
        {
            FoundryRuntimeFailureKind.TensorRtEngineLoad => retryAttempted
                ? "Foundry TensorRT engine load failed after a runtime reset retry."
                : "Foundry TensorRT engine load failed.",
            FoundryRuntimeFailureKind.TensorRtGpuOutOfMemory => "Foundry TensorRT execution ran out of GPU memory.",
            FoundryRuntimeFailureKind.ModelLoad => retryAttempted
                ? "Foundry model load failed after a runtime reset retry."
                : "Foundry model load failed.",
            FoundryRuntimeFailureKind.Execution => "Foundry runtime execution failed.",
            _ => "Foundry runtime failed."
        };

    private static IReadOnlyList<string> BuildNotes(
        FoundryRuntimeFailureKind failureKind,
        string diagnosticDetail,
        string? modelCacheRoot,
        string? logsRoot,
        bool retryAttempted,
        IReadOnlyList<string>? additionalNotes)
    {
        var notes = new List<string>();
        if (retryAttempted)
        {
            notes.Add("The app already retried once after resetting the in-process Foundry runtime.");
        }

        switch (failureKind)
        {
            case FoundryRuntimeFailureKind.TensorRtEngineLoad:
                if (!string.IsNullOrWhiteSpace(modelCacheRoot))
                {
                    notes.Add($"If this keeps recurring, stop the app and reset the affected Foundry model variant under '{modelCacheRoot}'.");
                }

                break;

            case FoundryRuntimeFailureKind.TensorRtGpuOutOfMemory:
                notes.Add("Free VRAM or switch to a smaller or non-TensorRT model variant before retrying.");
                break;
        }

        if (!string.IsNullOrWhiteSpace(diagnosticDetail))
        {
            notes.Add($"Runtime detail: {diagnosticDetail}");
        }

        if (!string.IsNullOrWhiteSpace(logsRoot))
        {
            notes.Add($"Foundry runtime logs are written under '{logsRoot}'.");
        }

        if (additionalNotes is { Count: > 0 })
        {
            notes.AddRange(additionalNotes.Where(static note => !string.IsNullOrWhiteSpace(note)));
        }

        return notes.Count == 0
            ? Array.Empty<string>()
            : notes.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> EnumerateMessages(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                messages.Add(current.Message.Trim());
            }
        }

        return messages;
    }

    private static string GetTerminalMessage(Exception exception)
    {
        string? terminalMessage = null;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                terminalMessage = current.Message.Trim();
            }
        }

        return string.IsNullOrWhiteSpace(terminalMessage)
            ? exception.Message
            : terminalMessage;
    }

    private static bool ContainsAny(IReadOnlyList<string> messages, IReadOnlyList<string> markers)
    {
        foreach (var message in messages)
        {
            foreach (var marker in markers)
            {
                if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}