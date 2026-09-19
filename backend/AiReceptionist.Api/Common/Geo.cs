namespace AiReceptionist.Api.Common;

/// <summary>
/// The rectangle OpenStreetMap draws around a place — what "the whole city" actually means.
///
/// This exists because comparing city *names* cannot be trusted. An address on Downing Street
/// reports its city as "City of Westminster"; searching for London gives back "Greater London";
/// an owner types "London". No amount of string tidying makes those three the same word, and a
/// business covering all of London would turn away half of it. They do all sit inside one
/// rectangle, so that is what the rule is measured against.
///
/// A rectangle is not a municipal boundary and reaches slightly past the real edges at the
/// corners. That bias is the right way round: the worst it does is take a booking somebody has to
/// ring back about, which is what happens just outside the line anyway.
/// </summary>
public record GeoBounds(double South, double North, double West, double East)
{
    /// <summary>Beyond this a box is a county, a state or a country rather than a city, and
    /// "the whole city" would quietly come to mean the whole region. Roughly 200 miles of
    /// latitude — bigger than any city and far smaller than the areas worth refusing.</summary>
    private const double MaxSpanDegrees = 3;

    public bool IsCitySized => North > South && East > West &&
                               North - South <= MaxSpanDegrees && East - West <= MaxSpanDegrees;

    public bool Contains(double latitude, double longitude) =>
        latitude >= South && latitude <= North && longitude >= West && longitude <= East;

    /// <summary>Reads the four numbers back off a stored row, or null when the row predates them
    /// or the geocoder gave a box too big to be a city.</summary>
    public static GeoBounds? From(double? south, double? north, double? west, double? east) =>
        south is { } s && north is { } n && west is { } w && east is { } e &&
        new GeoBounds(s, n, w, e) is { IsCitySized: true } bounds
            ? bounds
            : null;
}

/// <summary>Great-circle arithmetic and the small amount of text tidying the coverage rules need.</summary>
public static class Geo
{
    private const double EarthRadiusMiles = 3958.7613;
    public const double KmPerMile = 1.609344;

    /// <summary>Distance between two points in statute miles (haversine). Good to a few metres at
    /// city scale, which is far finer than a coverage radius anybody types.</summary>
    public static double MilesBetween(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double deg) => deg * Math.PI / 180d;

        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return EarthRadiusMiles * 2 * Math.Asin(Math.Min(1d, Math.Sqrt(a)));
    }

    public static double ToKm(double miles) => miles * KmPerMile;

    /// <summary>Whether two city names are the same place.
    ///
    /// One side was typed by an owner and the other comes out of OpenStreetMap, which answers with
    /// whichever administrative level happens to carry the name — "New York", "New York City" and
    /// "City of New York" are one place. Comparing them strictly would put a caller two streets
    /// from the branch in a different city, so case, accents, punctuation and the "city of"
    /// wrapper are all ignored, and an extra trailing word is tolerated ("Frankfurt" against
    /// "Frankfurt am Main").
    ///
    /// The match is on whole words from the start, never a bare substring: "York" sits inside
    /// "New York" and is a different city on a different continent, so a business with branches in
    /// both countries would otherwise have one silently claim the other's callers.
    ///
    /// This is the fallback, not the rule. <see cref="GeoBounds"/> is what decides whether an
    /// address is in a city; names are only consulted for a branch saved before the boundary was
    /// stored, or one the geocoder gave no usable box for.</summary>
    public static bool SameCity(string? a, string? b)
    {
        var x = NormalizeCity(a);
        var y = NormalizeCity(b);
        if (x.Length == 0 || y.Length == 0) return false;
        return x == y ||
               x.StartsWith(y + " ", StringComparison.Ordinal) ||
               y.StartsWith(x + " ", StringComparison.Ordinal);
    }

    /// <summary>Lower case, unaccented, punctuation-free words separated by single spaces, with
    /// the "city of X" and "X city" wrappers taken off.</summary>
    private static string NormalizeCity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        // Punctuation becomes a space rather than vanishing, so "Stratford-upon-Avon" stays three
        // words and cannot merge into something that matches a different place.
        var folded = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            folded.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }

        var words = folded.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 2 && words[0] == "city" && words[1] == "of") words.RemoveRange(0, 2);
        if (words.Count > 1 && words[^1] == "city") words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }
}
