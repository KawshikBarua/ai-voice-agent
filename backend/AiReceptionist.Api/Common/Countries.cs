using System.Globalization;

namespace AiReceptionist.Api.Common;

public record Country(string Code, string Name);

/// <summary>
/// The ISO 3166-1 country list, taken from the runtime's own globalization data rather than a
/// file in this repository — there is nothing to keep up to date, and the codes are guaranteed to
/// be the ones <see cref="RegionInfo"/> will accept when a name is looked up again.
///
/// The list is what the branch picker offers and what the server validates against, so a country
/// code that reaches the database is always one the geocoder can be restricted to.
/// </summary>
public static class Countries
{
    private static readonly Dictionary<string, Country> ByCode = Build();

    public static readonly IReadOnlyList<Country> All =
        ByCode.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The English name for an alpha-2 code, or null when it is not a country.</summary>
    public static string? NameFor(string? code) =>
        code is { Length: 2 } && ByCode.TryGetValue(code.ToUpperInvariant(), out var country) ? country.Name : null;

    private static Dictionary<string, Country> Build()
    {
        var map = new Dictionary<string, Country>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                // Several cultures map to one country ("en-US", "es-US"), so first one wins.
                if (region.TwoLetterISORegionName.Length == 2)
                    map.TryAdd(region.TwoLetterISORegionName.ToUpperInvariant(),
                        new Country(region.TwoLetterISORegionName.ToUpperInvariant(), region.EnglishName));
            }
            catch (ArgumentException)
            {
                // Not every specific culture has a region (e.g. the invariant one). Skip it.
            }
        }
        return map;
    }
}
