using System.Text.Json;

namespace AiReceptionist.Api.Common;

/// <summary>Appointments are stored in UTC; callers and the AI speak in the tenant's
/// local time (Organizations.Timezone, IANA or Windows id). These helpers convert
/// between the two and resolve the tenant's business hours for a given weekday.</summary>
public static class TenantTime
{
    public static TimeZoneInfo Resolve(string? timezoneId)
    {
        if (string.IsNullOrWhiteSpace(timezoneId)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(timezoneId); }
        catch { return TimeZoneInfo.Utc; }
    }

    public static DateTime ToUtc(DateTime local, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);

    public static DateTime ToLocal(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);

    public static DateTime NowLocal(TimeZoneInfo tz) => ToLocal(DateTime.UtcNow, tz);
}

public record DayWindow(TimeSpan Start, TimeSpan End);

/// <summary>Parses BusinessHoursJson like {"mon-fri":"09:00-17:00","sat":"10:00-14:00","sun":"closed"}.
/// Keys are single days ("mon") or ranges ("mon-fri"); values "HH:mm-HH:mm" or "closed".
/// Missing/invalid config falls back to 09:00–17:00 every day; a configured JSON that
/// omits a day treats that day as closed.</summary>
public static class BusinessHours
{
    private static readonly string[] DayKeys = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    private static readonly DayWindow Default = new(TimeSpan.FromHours(9), TimeSpan.FromHours(17));

    public static DayWindow? For(string? json, DayOfWeek day)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var dayKey = DayKeys[(int)day];
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var key = prop.Name.Trim().ToLowerInvariant();
                if (key != dayKey && !(key.Contains('-') && RangeContains(key, dayKey))) continue;

                var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                if (string.IsNullOrWhiteSpace(value) || value.Equals("closed", StringComparison.OrdinalIgnoreCase))
                    return null;

                var parts = value.Split('-');
                if (parts.Length == 2 &&
                    TimeSpan.TryParse(parts[0], out var start) &&
                    TimeSpan.TryParse(parts[1], out var end) && end > start)
                    return new DayWindow(start, end);
                return Default;
            }
            return null; // configured, but this day not listed → closed
        }
        catch
        {
            return Default;
        }
    }

    /// <summary>The weekly schedule as one readable line per day, Monday first. The stored JSON
    /// is a storage format — asking an LLM to interpret it mid-call (and read it out to a caller)
    /// is how agents end up inventing hours. Reflects the same defaults <see cref="For"/> applies,
    /// so what the agent is told always matches what the booking tools actually enforce.</summary>
    public static IEnumerable<string> Describe(string? json)
    {
        DayOfWeek[] weekOrder =
        [
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
        ];

        foreach (var day in weekOrder)
        {
            var window = For(json, day);
            yield return window is null
                ? $"{day}: closed"
                : $"{day}: {window.Start:hh\\:mm}-{window.End:hh\\:mm}";
        }
    }

    private static bool RangeContains(string range, string dayKey)
    {
        var ends = range.Split('-');
        if (ends.Length != 2) return false;
        int a = Array.IndexOf(DayKeys, ends[0].Trim());
        int b = Array.IndexOf(DayKeys, ends[1].Trim());
        int d = Array.IndexOf(DayKeys, dayKey);
        if (a < 0 || b < 0 || d < 0) return false;
        return a <= b ? d >= a && d <= b : d >= a || d <= b; // supports wrapped ranges like fri-mon
    }
}
