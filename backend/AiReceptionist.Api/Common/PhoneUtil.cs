using System.Text.RegularExpressions;

namespace AiReceptionist.Api.Common;

/// <summary>Canonical phone format: optional leading '+' followed by digits only
/// (e.g. "+1 555-010-0777" → "+15550100777"). Applied at every write and lookup so
/// Retell's E.164 numbers, dictated numbers and hand-typed numbers all match.</summary>
public static class PhoneUtil
{
    // '+', a non-zero country code, then 7-14 more digits (E.164 caps the total at 15).
    // ASCII ranges, not \d: char.IsDigit accepts Unicode digits (Arabic-Indic ٠١٢ and friends)
    // and .NET's \d matches them too, which would let non-ASCII characters through to a URL.
    private static readonly Regex E164 = new(@"^\+[1-9][0-9]{7,14}$", RegexOptions.Compiled);

    // How people actually type a number: digits plus the usual grouping punctuation.
    private static readonly Regex TypedNumber = new(@"^[0-9+()\-.\s]+$", RegexOptions.Compiled);

    public static string Normalize(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "";
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return phone.TrimStart().StartsWith('+') ? "+" + digits : digits;
    }

    /// <summary>Strict E.164 form, or null when the value is blank or not a valid international
    /// number. Use this (never <see cref="Normalize"/>) wherever a caller-supplied number is
    /// handed to an external API or placed in a URL: the result is guaranteed to be nothing but
    /// '+' and digits, so it cannot carry a path segment, query string or control character.</summary>
    public static string? ToE164(string? phone)
    {
        if (phone is null) return null;

        // Reject characters that do not belong in a typed phone number instead of dropping them:
        // Normalize keeps only the digits, which would quietly turn "+15551234567?x=1" into
        // "+155512345671" — a different, valid-looking number that routes calls elsewhere.
        if (!TypedNumber.IsMatch(phone)) return null;

        var normalized = Normalize(phone);
        return E164.IsMatch(normalized) ? normalized : null;
    }
}
