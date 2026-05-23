namespace LiCvWriter.Application.Models;

/// <summary>
/// Classifies user-actionable Foundry Local runtime failures so the app can
/// surface them without collapsing everything into a generic benchmark error.
/// </summary>
public enum FoundryRuntimeFailureKind
{
    /// <summary>
    /// The failure did not match a known Foundry runtime pattern.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The TensorRT execution provider could not deserialize or load its engine.
    /// </summary>
    TensorRtEngineLoad,

    /// <summary>
    /// The TensorRT execution provider ran out of GPU memory during execution.
    /// </summary>
    TensorRtGpuOutOfMemory,

    /// <summary>
    /// Foundry failed while loading the model for use.
    /// </summary>
    ModelLoad,

    /// <summary>
    /// Foundry failed while executing a chat completion after load succeeded.
    /// </summary>
    Execution
}