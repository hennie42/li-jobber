namespace LiCvWriter.Application.Models;

/// <summary>
/// Indicates whether the live benchmark session is currently monitoring a suspected stall.
/// </summary>
public enum ModelBenchmarkHangState
{
    None,
    Warning
}