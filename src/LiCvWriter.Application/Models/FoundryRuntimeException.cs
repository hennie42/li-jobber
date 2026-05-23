namespace LiCvWriter.Application.Models;

/// <summary>
/// Represents a Foundry Local runtime failure that has already been normalized
/// into user-facing diagnostics and optional recovery notes.
/// </summary>
public sealed class FoundryRuntimeException : InvalidOperationException
{
    /// <summary>
    /// Creates a new classified Foundry runtime exception.
    /// </summary>
    public FoundryRuntimeException(
        FoundryRuntimeFailureKind failureKind,
        string message,
        bool retryAttempted,
        IReadOnlyList<string>? notes = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
        RetryAttempted = retryAttempted;
        Notes = notes is { Count: > 0 } ? [.. notes] : Array.Empty<string>();
    }

    /// <summary>
    /// Gets the classified Foundry runtime failure kind.
    /// </summary>
    public FoundryRuntimeFailureKind FailureKind { get; }

    /// <summary>
    /// Gets a value indicating whether the app already retried after a runtime reset.
    /// </summary>
    public bool RetryAttempted { get; }

    /// <summary>
    /// Gets tooltip-friendly recovery and diagnostic notes for the failure.
    /// </summary>
    public IReadOnlyList<string> Notes { get; }
}