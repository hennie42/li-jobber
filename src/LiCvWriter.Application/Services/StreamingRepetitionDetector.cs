using System.Text;

namespace LiCvWriter.Application.Services;

/// <summary>
/// Detects short tail cycles that repeat enough times to indicate a streaming repetition loop.
/// </summary>
public static class StreamingRepetitionDetector
{
    private const int RepetitionMinCycleLength = 1;
    private const int RepetitionMaxCycleLength = 24;
    private const int RepetitionRequiredRepeats = 4;

    /// <summary>
    /// Returns true when the trailing portion of a streamed text buffer has entered a short repeated cycle.
    /// </summary>
    public static bool DetectRepetitionLoop(StringBuilder buffer, int minLength)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (minLength <= 0 || buffer.Length < minLength)
        {
            return false;
        }

        var tailLength = Math.Min(buffer.Length, RepetitionMaxCycleLength * (RepetitionRequiredRepeats + 1));
        var tailStart = buffer.Length - tailLength;
        var tail = buffer.ToString(tailStart, tailLength);

        for (var cycleLength = RepetitionMinCycleLength; cycleLength <= RepetitionMaxCycleLength; cycleLength++)
        {
            if (tail.Length < cycleLength * RepetitionRequiredRepeats)
            {
                break;
            }

            var candidate = tail[^cycleLength..];
            var matched = 0;

            for (var offset = tail.Length - cycleLength; offset >= cycleLength; offset -= cycleLength)
            {
                var segment = tail.Substring(offset - cycleLength, cycleLength);
                if (!segment.Equals(candidate, StringComparison.Ordinal))
                {
                    break;
                }

                matched++;
            }

            if (matched >= RepetitionRequiredRepeats - 1)
            {
                return true;
            }
        }

        return false;
    }
}