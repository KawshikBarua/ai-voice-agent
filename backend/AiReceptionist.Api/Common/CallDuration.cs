namespace AiReceptionist.Api.Common;

/// <summary>
/// How long a Retell call lasted, in seconds — the one number every AI minute is billed from.
///
/// It used to be derived from <c>end_timestamp - start_timestamp</c> alone, and fell to zero the
/// moment either was missing or arrived out of order. Zero seconds is zero minutes, so a payload
/// this platform could not read was a call given away for nothing, silently. Retell sends
/// <c>duration_ms</c> as well, which is its own measurement rather than a subtraction of two
/// clocks, so it is tried first and the timestamps are the fallback.
///
/// Shared by the live webhook and the backfill importer deliberately: a call counted one way when
/// it arrives and another way when it is re-imported is a customer whose bill changes for no reason
/// they can see.
/// </summary>
public static class CallDuration
{
    /// <summary>Seconds of talk time, or 0 when nothing in the payload can say.</summary>
    public static int Resolve(long startTimestampMs, long endTimestampMs, long durationMs)
    {
        // Retell's own figure, when it sent one.
        if (durationMs > 0) return (int)(durationMs / 1000);

        // Fall back to the span between the two clocks. Ordered, because an end before a start is a
        // payload that means nothing, and a negative duration would credit the customer minutes.
        if (startTimestampMs > 0 && endTimestampMs > startTimestampMs)
            return (int)((endTimestampMs - startTimestampMs) / 1000);

        return 0;
    }
}
