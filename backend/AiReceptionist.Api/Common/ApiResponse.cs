using System.Globalization;

namespace AiReceptionist.Api.Common;

/// <summary>Standardized API envelope (SRS §21 – API Response Standard).</summary>
public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public T? Data { get; set; }
    public List<string> Errors { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string TraceId { get; set; } = string.Empty;

    public static ApiResponse<T> Ok(T data, string message = "OK") =>
        new() { Success = true, Message = message, Data = data };

    public static ApiResponse<T> Fail(string message, params string[] errors) =>
        new() { Success = false, Message = message, Errors = errors.ToList() };
}

public class PagedResult<T>
{
    public IEnumerable<T> Items { get; set; } = Enumerable.Empty<T>();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>One slice of a keyset-paged list. There is deliberately no total: counting the
/// matches costs a scan of every one of them, which is the work this shape exists to avoid.</summary>
public class CursorPage<T>
{
    public IEnumerable<T> Items { get; set; } = Enumerable.Empty<T>();
    /// <summary>Opaque position of the last item; null once the list is exhausted.</summary>
    public string? NextCursor { get; set; }
}

/// <summary>
/// Where a page of a keyset-paged list ended: the sort key of its last row — a timestamp, then
/// the id that breaks ties on it. It carries no authority — every query it feeds is still bound
/// to the caller's organization — so it is only validated for shape, and the worst a forged one
/// can do is start the caller's own list somewhere else.
/// </summary>
public readonly record struct KeysetCursor(DateTime? At, int Id)
{
    public override string ToString() =>
        $"{At?.Ticks.ToString(CultureInfo.InvariantCulture)}.{Id.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParse(string? value, out KeysetCursor cursor)
    {
        cursor = default;
        var parts = value?.Split('.');
        if (parts is not { Length: 2 } ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return false;

        if (parts[0].Length == 0)
        {
            cursor = new KeysetCursor(null, id);
            return true;
        }
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks > DateTime.MaxValue.Ticks)
            return false;

        cursor = new KeysetCursor(new DateTime(ticks), id);
        return true;
    }
}
