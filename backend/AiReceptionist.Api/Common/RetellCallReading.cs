using System.Text.Json;

namespace AiReceptionist.Api.Common;

/// <summary>
/// One Retell call object, read once, the same way by every path that records calls — the live
/// webhook and the import from <c>/v2/list-calls</c>.
///
/// The two used to parse the payload separately, and neither asked whether the call had finished.
/// The import runs every few minutes and lists calls that are still in progress; one of those has a
/// start but no end, so it was recorded at zero seconds, and when the real <c>call_ended</c> arrived
/// the webhook saw the row already there and discarded the finished call as a duplicate. The call
/// stayed at zero minutes for good while the call list and Retell both said otherwise.
/// <see cref="IsFinished"/> is what stops that, and <see cref="Status"/> being null when the payload
/// does not say how the call ended is what keeps a correction from guessing.
/// </summary>
public sealed class RetellCallReading
{
    public string? CallId { get; init; }
    public string? AgentId { get; init; }

    /// <summary>Retell's own state: registered, not_connected, ongoing, ended or error.</summary>
    public string? CallStatus { get; init; }

    /// <summary>The caller's number, normalized. Empty when the payload has none.</summary>
    public string FromNumber { get; init; } = "";

    public long StartTimestampMs { get; init; }
    public long EndTimestampMs { get; init; }
    public long DurationMs { get; init; }

    /// <summary>Talk time in whole seconds — the one number minutes are billed from. See
    /// <see cref="CallDuration.Resolve"/>.</summary>
    public int DurationSeconds { get; init; }

    public string? DisconnectionReason { get; init; }
    public string? Transcript { get; init; }
    public string? RecordingUrl { get; init; }
    public string? Summary { get; init; }

    /// <summary>
    /// True once Retell will not change this call's length again.
    ///
    /// Retell says so outright through <c>call_status</c>. Should a payload ever omit it, a call
    /// that has an end time (or Retell's own measured duration) is over, and one that has neither
    /// is not — recording it then is exactly how a live call gets filed at zero seconds.
    /// </summary>
    public bool IsFinished => CallStatus switch
    {
        "ended" or "error" or "not_connected" => true,
        "registered" or "ongoing" => false,
        _ => EndTimestampMs > 0 || DurationMs > 0,
    };

    /// <summary>
    /// How the call ended, as this platform labels it — or null when the payload does not say.
    /// Null is deliberate: a new row falls back to "Completed", but a correction to an existing row
    /// leaves its status alone rather than overwriting "Transferred" with a guess.
    /// </summary>
    public string? Status => DisconnectionReason switch
    {
        null or "" => null,
        "dial_no_answer" or "dial_busy" or "dial_failed" => "Missed",
        "call_transfer" => "Transferred",
        _ => "Completed",
    };

    /// <summary>When the call started, or now when the payload does not say.</summary>
    public DateTime StartedAtUtc => StartTimestampMs > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(StartTimestampMs).UtcDateTime
        : DateTime.UtcNow;

    public static RetellCallReading From(JsonElement call)
    {
        var start = GetLong(call, "start_timestamp");
        var end = GetLong(call, "end_timestamp");
        var durationMs = GetLong(call, "duration_ms");

        return new RetellCallReading
        {
            CallId = GetString(call, "call_id"),
            AgentId = GetString(call, "agent_id"),
            CallStatus = GetString(call, "call_status"),
            FromNumber = PhoneUtil.Normalize(GetString(call, "from_number")),
            StartTimestampMs = start,
            EndTimestampMs = end,
            DurationMs = durationMs,
            DurationSeconds = CallDuration.Resolve(start, end, durationMs),
            DisconnectionReason = GetString(call, "disconnection_reason"),
            Transcript = GetString(call, "transcript"),
            RecordingUrl = GetString(call, "recording_url"),
            Summary = call.TryGetProperty("call_analysis", out var analysis)
                ? GetString(analysis, "call_summary")
                : null,
        };
    }

    private static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// A whole number of milliseconds. Tolerates a fractional value rather than throwing: a
    /// <see cref="FormatException"/> here would fail the webhook on every retry, and a call whose
    /// every delivery fails is a call that is never recorded — or billed — at all.
    /// </summary>
    private static long GetLong(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object
            || !el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
            return 0;
        if (v.TryGetInt64(out var whole)) return whole;
        return v.TryGetDouble(out var fractional) && double.IsFinite(fractional)
            ? (long)Math.Round(fractional, MidpointRounding.AwayFromZero)
            : 0;
    }
}
