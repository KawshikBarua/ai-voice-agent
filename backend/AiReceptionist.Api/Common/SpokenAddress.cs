using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AiReceptionist.Api.Common;

/// <summary>
/// Turns what someone said on the phone into something a geocoder can match, without losing what
/// they actually said.
///
/// Nobody dictates an address. They give a street and a number and stop, because they rang a local
/// company and assume it knows the town. They name the shop next door instead of a number. And
/// once the booking feels settled they add the part they never thought of as the address at all —
/// the gate code, the suite number, "come round the back". All of that arrives in one string.
///
/// The access detail is the dangerous half. "1220 Grand, gate code 4432, round the back" matches
/// nothing: the geocoder is not holding a gate code, so the whole address comes back unfound and a
/// real customer is told their address does not exist. Stripping it is what keeps the lookup on
/// the part of the sentence that is an address — and the caller's own words are kept separately,
/// because the tidied string is the right thing to search with and the wrong thing to read back.
/// </summary>
public static class SpokenAddress
{
    private static readonly RegexOptions Opts =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);

    /// <summary>The parts of a spoken address that are instructions for the engineer rather than
    /// anything a map holds. Each one is removed wherever it appears.
    ///
    /// Written to be under-eager on purpose. Leaving a stray word in costs a slightly fuzzier
    /// match, which the geocoder absorbs; taking out a word that was part of the street name
    /// costs the address. Anything ambiguous is left alone.</summary>
    private static readonly Regex[] AccessDetail =
    [
        // "(round the back)", "(ring twice)" — a whole aside, however it is worded.
        new(@"\([^)]*\)", Opts, Budget),

        // Codes of every flavour: gate code 4432, entry code is 1234, keypad 99B, door code #7.
        new(@"\b(gate|door|entry|entrance|access|key\s?pad|keypad|lock\s?box|lockbox|security|call\s?box|callbox)\s*(code|number|box)?\s*(is|=|:)?\s*#?\s*[a-z0-9][a-z0-9\-]{0,9}\b", Opts, Budget),
        new(@"\bcode\s*(is|=|:)?\s*#?\s*[a-z0-9][a-z0-9\-]{0,9}\b", Opts, Budget),

        // How to get in once you are there.
        new(@"\b((come|go|walk|drive|head|pull|enter)\s+)?(round|around|out|in|at|to|through)\s+(the\s+)?(back|rear|side|alley|alleyway|yard|driveway|drive\s?way)\b", Opts, Budget),
        new(@"\b(rear|back|side|tradesman'?s?|service|staff)\s+(entrance|door|gate|entry)\b", Opts, Budget),
        // "ring" and "press" are left to their specific objects on purpose: Ring Road is a street
        // in half the towns in Britain, and stripping it would lose the address entirely.
        new(@"\b(buzz(er)?)\s+(the\s+|on\s+|for\s+)?[a-z0-9][a-z0-9\-]{0,9}\b", Opts, Budget),
        new(@"\b(ring|press|push|knock\s+on)\s+(the\s+)?(bell|buzzer|door\s?bell|button|intercom)\b", Opts, Budget),
        new(@"\b(ring|knock)\s+(twice|once|loudly|hard)\b", Opts, Budget),
        new(@"\b(use|go\s+to|come\s+to|take)\s+the\s+(back|rear|side|second|other)\s+\w+\b", Opts, Budget),
        new(@"\b(park|parking)\s+(in|on|at|out)\s+(the\s+)?\w+\b", Opts, Budget),
        new(@"\b(mind|watch\s+out\s+for|beware\s+of)\s+the\s+\w+\b", Opts, Budget),
        new(@"\b(dog|dogs)\s+(in|out)\s+the\s+\w+\b", Opts, Budget),

        // Sub-addresses. A geocoder holds the building, not the unit inside it, so these only
        // ever make the match worse — they are kept in the spoken text the engineer reads.
        // "fl", "lvl" and "rm" are deliberately absent: "Miami, FL 33101" is a state and a ZIP, not
        // a floor, and taking those out would throw away the very words that place the address.
        //
        // What follows the word has to look like a unit — a number, or a single letter — and never
        // just any word. "The unit behind Travis Perkins" is a caller describing where they are, and
        // reading "behind" as a suite number would strip the only landmark in the sentence.
        new(@"\b(flat|apt|apartment|unit|suite|ste|room|office|floor|level|bldg|building|block)\s*\.?\s*#?\s*(\d+\s*[a-z]?|[a-z]\s*-?\s*\d[a-z0-9\-]*|[a-z])\b", Opts, Budget),
        new(@"#\s*[a-z0-9][a-z0-9\-]{0,5}\b", Opts, Budget),
    ];

    /// <summary>Trailing junk left once a phrase has been lifted out of the middle of a sentence.</summary>
    private static readonly Regex Separators = new(@"[,;]\s*(?=[,;])|^[\s,;.\-–—]+|[\s,;.\-–—]+$", Opts, Budget);
    private static readonly Regex Whitespace = new(@"\s{2,}", Opts, Budget);

    /// <summary>What to put in front of the geocoder. Falls back to the caller's own words whenever
    /// stripping would leave too little to search on — an address that is mostly access detail is
    /// still better searched whole than searched as three characters.</summary>
    public static string ForGeocoder(string? spoken)
    {
        var original = (spoken ?? "").Trim();
        if (original.Length == 0) return "";

        var text = original;
        try
        {
            foreach (var pattern in AccessDetail)
                text = pattern.Replace(text, " ");
        }
        catch (RegexMatchTimeoutException)
        {
            // The text came off a phone-call transcript. If it is pathological enough to run the
            // matcher out of time, search it as it arrived rather than not at all.
            return original;
        }

        text = Whitespace.Replace(text, " ");
        text = Separators.Replace(text, "").Trim();
        // A stray comma pair left where a phrase used to be ("1220 Grand, , Kansas City").
        text = Whitespace.Replace(text.Replace(", ,", ","), " ").Trim(' ', ',', ';', '.', '-');

        // Stripping is only worth it while something recognisable survives. "Gate code 4432" is
        // all access detail, and an empty query answers nothing; the original at least has a chance.
        return text.Length >= 5 && HasLetter(text) ? text : original;
    }

    /// <summary>
    /// An address split around its street, so the pieces can be searched for separately.
    ///
    /// OpenStreetMap matches free text only when every word belongs to one place, and people do not
    /// talk like that. They put the plaza, building or shop before the number ("Ridge Park Square,
    /// 4794 Ridge Road"), or after the street a neighbourhood the map files under a different name.
    /// The plaza and the number are separate things on the map, so together they match nothing, and
    /// a real address is reported as not existing. Split apart, each piece can still be found.
    /// </summary>
    /// <param name="Lead">Whatever was named before the street: a plaza, building or shop. Often empty.</param>
    /// <param name="Number">The house number, or empty.</param>
    /// <param name="Street">The street — or, with no house number anywhere, the first thing said.</param>
    /// <param name="Trail">Whatever followed the street, usually the town. Often empty.</param>
    public sealed record AddressParts(string Lead, string Number, string Street, string Trail)
    {
        /// <summary>"4794 Ridge Road": the street with its number, or the street alone.</summary>
        public string Numbered => Number.Length == 0 ? Street : $"{Number} {Street}";

        /// <summary>Everything except the name in front: "4794 Ridge Road, Brooklyn".</summary>
        public string NumberedInTown => Join(Numbered, Trail);
    }

    /// <summary>Splits a geocoder query around its first house number. With no house number, a query
    /// in two or more comma-separated parts is split after the first. Null when there is nothing to
    /// split: the query as given is already the only thing to search for.</summary>
    public static AddressParts? Parse(string? query)
    {
        var parts = (query ?? "").Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            for (var i = 0; i < parts.Length; i++)
            {
                var lead = "";
                var match = NumberedStreet.Match(parts[i]);
                if (!match.Success)
                {
                    var named = NamedThenNumbered.Match(parts[i]);
                    if (!named.Success || !HasLetter(named.Groups["lead"].Value) ||
                        RoadNumber.IsMatch(named.Groups["lead"].Value.Trim())) continue;

                    lead = named.Groups["lead"].Value.Trim();
                    match = NumberedStreet.Match(named.Groups["rest"].Value);
                    if (!match.Success) continue;
                }

                return new AddressParts(Join([.. parts[..i], lead]), match.Groups["number"].Value,
                    match.Groups["street"].Value.Trim(), Join(parts[(i + 1)..]));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Same stance as ForGeocoder: a transcript pathological enough to run the matcher out
            // of time is searched as it arrived, with no clever second guesses.
            return null;
        }

        return parts.Length >= 2 ? new AddressParts("", "", parts[0], Join(parts[1..])) : null;
    }

    /// <summary>A house number and the street it belongs to: "4794 Ridge Road", "12B Baker St",
    /// "100 5th Ave". The street has to start with a word or an ordinal, so a bare "12" or a ZIP
    /// code is never taken for one.</summary>
    private static readonly Regex NumberedStreet = new(
        @"^(?<number>\d+[a-z]?(?:\s*-\s*\d+[a-z]?)?)\s+(?<street>(?:\p{L}|\d+(?:st|nd|rd|th)\b).*)$", Opts, Budget);

    /// <summary>The same, preceded by a name in the same breath: "Ridge Park Square 4794 Ridge Road".</summary>
    private static readonly Regex NamedThenNumbered = new(
        @"^(?<lead>[^\d]+?)\s+(?<rest>\d.*)$", Opts, Budget);

    /// <summary>Words whose number belongs to the road rather than to a house — Route 9, Highway 101,
    /// County Road 12. What follows those is not a street to search for on its own.</summary>
    private static readonly Regex RoadNumber = new(
        @"\b(highway|hwy|route|rte|rt|road|rd|interstate|state|county|township|twp|us|sr|cr|fm|box|exit|mile)\.?$", Opts, Budget);

    private static readonly char[] SegmentSeparators = [',', ';'];

    private static string Join(params string[] parts) =>
        string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>Whether the caller named this place themselves.
    ///
    /// The whole question behind a second, local lookup: a caller who said "Manchester" and got
    /// Manchester named their town, and being told it is out of area is a real answer. A caller who
    /// said "1220 Grand" and got a city they never mentioned had one picked for them, and that is
    /// worth checking again closer to home before anyone is turned away.</summary>
    public static bool Mentions(string? spoken, string? place)
    {
        var haystack = Fold(spoken);
        var needle = Fold(place);
        // Two characters of a city name inside a street name is a coincidence, not a mention.
        if (haystack.Length == 0 || needle.Length < 3) return false;
        return haystack.Contains(needle, StringComparison.Ordinal);
    }

    private static bool HasLetter(string text)
    {
        foreach (var ch in text)
            if (char.IsLetter(ch)) return true;
        return false;
    }

    /// <summary>Lower case, unaccented, letters and digits only — so "St. Albans" still answers to
    /// "st albans" and "Bâle" to "bale".</summary>
    private static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }
}
