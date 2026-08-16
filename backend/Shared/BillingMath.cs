namespace AiReceptionist.Shared;

/// <summary>The billing cycles a plan can run on. Compiled into both server applications so the
/// console and the API can never disagree about when a period ends.</summary>
public static class BillingCycles
{
    public const string Monthly = "Monthly";
    public const string Yearly = "Yearly";

    public static readonly string[] All = [Monthly, Yearly];

    public static bool IsYearly(string? cycle) =>
        string.Equals(cycle, Yearly, StringComparison.OrdinalIgnoreCase);

    /// <summary>Anything unrecognised becomes Monthly rather than throwing: a bad value in the
    /// database must not stop a customer being billed.</summary>
    public static string Normalize(string? cycle) => IsYearly(cycle) ? Yearly : Monthly;

    /// <summary>Moves <paramref name="from"/> forward by one billing cycle.</summary>
    public static DateTime Advance(DateTime from, string? cycle) =>
        IsYearly(cycle) ? from.AddYears(1) : from.AddMonths(1);
}

/// <summary>
/// The windows AI minutes are counted in.
///
/// These are deliberately not the subscription's CurrentPeriodStart/End. That pair tracks *access*
/// — it only moves when a payment is recorded, and it deliberately stops moving when one is not,
/// which is what the grace period and the overdue sweep read. Usage has to keep being counted into
/// fresh windows regardless of whether the last invoice was paid, or an unpaid customer's minutes
/// would all pile into one window that never closes.
///
/// The anchor is therefore the end of the last period already closed (or, before any have been,
/// the subscription's current period start), and windows are one cycle each from there.
/// </summary>
public static class UsageWindows
{
    /// <summary>Enough iterations to catch up on a subscription left alone for years, and few
    /// enough that a nonsense anchor date cannot spin forever.</summary>
    private const int MaxWindows = 120;

    /// <summary>Every whole window between the anchor and now, oldest first. These are the periods
    /// that are ready to be totalled and charged.</summary>
    public static IEnumerable<(DateTime Start, DateTime End)> Elapsed(
        DateTime anchor, string? cycle, DateTime utcNow)
    {
        var start = anchor;
        for (var i = 0; i < MaxWindows; i++)
        {
            var end = BillingCycles.Advance(start, cycle);
            if (end > utcNow) yield break;
            yield return (start, end);
            start = end;
        }
    }

    /// <summary>The window now falls inside — the one the customer is currently running up.</summary>
    public static (DateTime Start, DateTime End) Current(DateTime anchor, string? cycle, DateTime utcNow)
    {
        var start = anchor;
        for (var i = 0; i < MaxWindows; i++)
        {
            var end = BillingCycles.Advance(start, cycle);
            if (end > utcNow) return (start, end);
            start = end;
        }
        return (start, BillingCycles.Advance(start, cycle));
    }
}

/// <summary>Stripe works in a currency's smallest unit. Most currencies have two decimal places,
/// but a handful have none at all — sending 1000 for ¥1,000 would otherwise charge ¥100,000.</summary>
public static class StripeMoney
{
    // https://docs.stripe.com/currencies#zero-decimal
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA",
        "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF",
    };

    public static long ToMinorUnits(decimal amount, string? currency) =>
        ZeroDecimal.Contains(currency ?? "")
            ? (long)Math.Round(amount, 0, MidpointRounding.AwayFromZero)
            : (long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    public static decimal FromMinorUnits(long minorUnits, string? currency) =>
        ZeroDecimal.Contains(currency ?? "") ? minorUnits : minorUnits / 100m;

    /// <summary>Rounds to the precision the currency can actually be charged in, so a stored
    /// amount and the amount Stripe collects always agree.</summary>
    public static decimal Round(decimal amount, string? currency) =>
        FromMinorUnits(ToMinorUnits(amount, currency), currency);
}
