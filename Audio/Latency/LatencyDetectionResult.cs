namespace BassRouter.Audio.Latency;

/// <param name="OffsetMs">How many milliseconds later the subwoofer plays than the primary output (negative if it plays earlier).</param>
/// <param name="HeadphoneDelayMs">The artificial latency set on the primary output.</param>
/// <param name="SubDelayMs">The artificial latency set on the subwoofer.</param>
public sealed record LatencyDetectionResult(double OffsetMs, int HeadphoneDelayMs, int SubDelayMs)
{
    /// <summary>
    /// Delays whichever output plays first by the difference, so both play in sync.
    /// </summary>
    public static LatencyDetectionResult FromOffset(double offsetSeconds)
    {
        double offsetMs = offsetSeconds * 1000;
        int delayMs = (int)Math.Round(Math.Abs(offsetMs));

        return offsetMs >= 0
            ? new LatencyDetectionResult(offsetMs, HeadphoneDelayMs: delayMs, SubDelayMs: 0)
            : new LatencyDetectionResult(offsetMs, HeadphoneDelayMs: 0, SubDelayMs: delayMs);
    }
}
