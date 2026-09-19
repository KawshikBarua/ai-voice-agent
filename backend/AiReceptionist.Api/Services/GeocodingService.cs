using System.Net.Http.Headers;
using System.Text.Json;
using AiReceptionist.Api.Common;
using Microsoft.Extensions.Caching.Memory;

namespace AiReceptionist.Api.Services;

/// <summary>A place OpenStreetMap recognised, with the parts the coverage rules read.</summary>
public record GeocodedPlace(string DisplayName, double Latitude, double Longitude, string? City, string? CountryCode);

/// <summary>Something worth naming near an address, and how far it is on foot.</summary>
public record Landmark(string Name, int Meters);

/// <summary>One option in the city picker. <paramref name="Region"/> is the state or county,
/// which is the only thing telling two identically named towns apart, and <paramref name="Bounds"/>
/// is the city's own rectangle — what "covers the whole city" ends up being measured against.</summary>
public record CitySuggestion(string City, string? Region, double Latitude, double Longitude, GeoBounds? Bounds);

/// <summary>
/// The result of a geocode, keeping apart the two failures that look identical from the outside
/// and mean opposite things.
///
/// <see cref="Place"/> null with <see cref="Unavailable"/> false is OpenStreetMap answering that
/// no such address exists — a real verdict, and grounds to refuse a booking. Both together mean
/// we never got an answer, and a booking must go ahead unchecked rather than every caller being
/// turned away because somebody else's server is down.
/// </summary>
public record GeocodeOutcome(GeocodedPlace? Place, bool Unavailable)
{
    public static readonly GeocodeOutcome Down = new(null, true);
    public static readonly GeocodeOutcome NoSuchPlace = new(null, false);
}

public interface IGeocodingService
{
    /// <summary>Resolves free text to a place, restricted to the given ISO country codes.</summary>
    Task<GeocodeOutcome> GeocodeAsync(string query, IReadOnlyCollection<string> countryCodes, CancellationToken ct = default);

    /// <summary>Cities and towns in one country matching what has been typed so far.</summary>
    Task<IReadOnlyList<CitySuggestion>> SearchCitiesAsync(string countryCode, string query, CancellationToken ct = default);

    /// <summary>Named places within walking distance, nearest first. Best-effort: an empty list
    /// means either there is nothing there or Overpass did not answer in time, and neither is
    /// allowed to hold up a booking.</summary>
    Task<IReadOnlyList<Landmark>> LandmarksNearAsync(double latitude, double longitude, CancellationToken ct = default);
}

/// <summary>
/// The OpenStreetMap side of coverage checking: Nominatim for geocoding, Overpass for landmarks.
/// Both are free public services run on donated capacity, and this class is written around what
/// that costs:
///
///  • <b>Called from the server only.</b> Never the browser — the usage policy needs one
///    identifiable User-Agent per application, and a caller's home address has no business
///    leaving this process to a third party from the customer's own machine.
///  • <b>One request per second, deployment-wide</b> (<see cref="OutboundThrottle"/>), which is
///    what Nominatim's policy asks for. Everything else queues behind it.
///  • <b>Cached hard.</b> A city does not move, and the same caller's address is looked up twice
///    within seconds — once when the agent checks the area and again when it books. The second
///    lookup is a cache hit, so enforcing the rule at booking time costs nothing on the call.
///  • <b>Short timeouts, no exceptions escape.</b> A geocode that fails answers "not found" and a
///    landmark lookup that fails answers "none": a live call must never be held open by somebody
///    else's outage.
///
/// Anything heavier than this belongs on a paid geocoder — Nominatim's own advice.
/// </summary>
public class GeocodingService : IGeocodingService
{
    private readonly IHttpClientFactory _factory;
    private readonly IConfiguration _config;
    private readonly ILogger<GeocodingService> _logger;

    // Its own cache rather than the shared one, so a bound can be put on it: geocoding keys are
    // attacker-influenced (an address read off a phone call), and an unbounded cache of those is
    // a slow memory leak with a stranger's hand on the tap.
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 20_000 });

    private static readonly OutboundThrottle NominatimGate = new(TimeSpan.FromSeconds(1));
    private static readonly OutboundThrottle OverpassGate = new(TimeSpan.FromSeconds(1));

    private static readonly TimeSpan AddressTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan PlaceTtl = TimeSpan.FromDays(7);

    public const string HttpClientName = "osm";

    public GeocodingService(IHttpClientFactory factory, IConfiguration config, ILogger<GeocodingService> logger)
    {
        _factory = factory;
        _config = config;
        _logger = logger;
    }

    private string NominatimUrl => _config["Osm:NominatimUrl"]?.TrimEnd('/') ?? "https://nominatim.openstreetmap.org";
    private string OverpassUrl => _config["Osm:OverpassUrl"] ?? "https://overpass-api.de/api/interpreter";
    private int WalkingMeters => Math.Clamp(_config.GetValue("Osm:LandmarkRadiusMeters", 800), 100, 2000);

    public async Task<GeocodeOutcome> GeocodeAsync(string query, IReadOnlyCollection<string> countryCodes, CancellationToken ct = default)
    {
        var text = Clean(query, 300);
        if (text.Length < 3) return GeocodeOutcome.NoSuchPlace;

        var countries = string.Join(",", countryCodes
            .Select(c => c.Trim().ToLowerInvariant())
            .Where(c => c.Length == 2)
            .Distinct()
            .Order());

        return await CachedAsync($"addr|{countries}|{text.ToLowerInvariant()}", AddressTtl, async () =>
        {
            var url = $"{NominatimUrl}/search?format=jsonv2&addressdetails=1&limit=1&q={Uri.EscapeDataString(text)}" +
                      (countries.Length > 0 ? $"&countrycodes={countries}" : "");

            var root = await GetJsonAsync(url, ct);
            if (root is not { ValueKind: JsonValueKind.Array } array) return (GeocodeOutcome.Down, Cacheable: false);

            return array.GetArrayLength() == 0 || ReadPlace(array[0]) is not { } place
                ? (GeocodeOutcome.NoSuchPlace, Cacheable: true)
                : (new GeocodeOutcome(place, false), Cacheable: true);
        }) ?? GeocodeOutcome.Down;
    }

    /// <summary>A floor on what is worth asking about, not a promise that four characters will
    /// find anything.
    ///
    /// Nominatim is a search engine, not an autocomplete — its own documentation says so. On a
    /// short fragment it falls back to matching what merely sounds similar ("Lon" in the United
    /// Kingdom returns Brookeborough and Falkirk, and no London), and on a genuine prefix it
    /// commonly returns nothing at all ("Manche" finds no Manchester). Below this the answers are
    /// noise, so the request is not made; above it the caller still has to type the real name.</summary>
    private const int MinCityQuery = 4;

    public async Task<IReadOnlyList<CitySuggestion>> SearchCitiesAsync(string countryCode, string query, CancellationToken ct = default)
    {
        var country = Clean(countryCode, 2).ToLowerInvariant();
        var text = Clean(query, 60);
        if (country.Length != 2 || text.Length < MinCityQuery) return [];

        return await CachedAsync<IReadOnlyList<CitySuggestion>>($"city|{country}|{text.ToLowerInvariant()}", PlaceTtl, async () =>
        {
            // featureType=settlement is Nominatim's own filter for inhabited places, so the list
            // comes back without the streets and shops a bare search would bury the cities under.
            var url = $"{NominatimUrl}/search?format=jsonv2&addressdetails=1&limit=10&featureType=settlement" +
                      $"&countrycodes={country}&city={Uri.EscapeDataString(text)}";

            var root = await GetJsonAsync(url, ct);
            if (root is not { ValueKind: JsonValueKind.Array } array) return (null, Cacheable: false);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<CitySuggestion>();
            foreach (var element in array.EnumerateArray())
            {
                if (ReadPlace(element) is not { City.Length: > 0 } place) continue;

                // Even at four characters the tail of the list drifts into places that only
                // sound alike, and an owner picking one of those would draw their coverage area
                // around the wrong town. A suggestion has to actually contain what was typed.
                if (!Resembles(place.City!, text) && !Resembles(place.DisplayName, text)) continue;

                var region = Text(element, "address", "state") ?? Text(element, "address", "county");
                if (!seen.Add($"{place.City}|{region}")) continue;

                results.Add(new CitySuggestion(place.City!, region, place.Latitude, place.Longitude,
                    ReadBounds(element)));
            }
            return ((IReadOnlyList<CitySuggestion>)results, Cacheable: true);
        }) ?? [];
    }

    /// <summary>Whether a suggestion is plausibly what was typed, ignoring case, accents and
    /// punctuation — so "Pen-Lôn" still answers to "lon" and "Greater London" to "london".</summary>
    private static bool Resembles(string candidate, string typed) =>
        Fold(candidate).Contains(Fold(typed), StringComparison.Ordinal);

    private static string Fold(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>The place's own rectangle, as [south, north, west, east] strings.</summary>
    private static GeoBounds? ReadBounds(JsonElement element)
    {
        if (!element.TryGetProperty("boundingbox", out var box) ||
            box.ValueKind != JsonValueKind.Array || box.GetArrayLength() != 4)
            return null;

        var edges = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(box[i].GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out edges[i]))
                return null;
        }
        return new GeoBounds(edges[0], edges[1], edges[2], edges[3]) is { IsCitySized: true } bounds
            ? bounds
            : null;
    }

    public async Task<IReadOnlyList<Landmark>> LandmarksNearAsync(double latitude, double longitude, CancellationToken ct = default)
    {
        // Rounded to ~11 m before it becomes a cache key. Two callers on the same street share an
        // answer, and the key stops being a record of exactly where somebody lives.
        var key = $"lm|{latitude:F4}|{longitude:F4}";

        return await CachedAsync<IReadOnlyList<Landmark>>(key, PlaceTtl, async () =>
        {
            var root = await PostOverpassAsync(OverpassQuery(latitude, longitude), ct);
            if (root is not { ValueKind: JsonValueKind.Object } body ||
                !body.TryGetProperty("elements", out var elements) ||
                elements.ValueKind != JsonValueKind.Array)
                return (null, Cacheable: false);

            var found = new List<Landmark>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in elements.EnumerateArray())
            {
                var name = Text(element, "tags", "name");
                if (name is null || !seen.Add(name)) continue;

                // Ways and relations carry their position under "center"; nodes have it directly.
                var lat = Number(element, "lat") ?? Number(element, "center", "lat");
                var lon = Number(element, "lon") ?? Number(element, "center", "lon");
                if (lat is null || lon is null) continue;

                var meters = (int)Math.Round(Geo.ToKm(Geo.MilesBetween(latitude, longitude, lat.Value, lon.Value)) * 1000);
                found.Add(new Landmark(name, meters));
            }

            return ((IReadOnlyList<Landmark>)found.OrderBy(l => l.Meters).Take(3).ToList(), Cacheable: true);
        }) ?? [];
    }

    // ---------- OpenStreetMap plumbing ----------

    /// <summary>The tags that make something worth saying out loud — a station, a park, a museum,
    /// the town hall. Deliberately a closed list: "anything with a name" within 800 m is mostly
    /// takeaways and bus stops, which is noise on a phone call rather than a landmark.</summary>
    private string OverpassQuery(double lat, double lon)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var around = $"(around:{WalkingMeters},{lat.ToString(invariant)},{lon.ToString(invariant)})";

        return string.Join("\n",
            "[out:json][timeout:8];",
            "(",
            $"  nwr{around}[\"name\"][\"tourism\"~\"^(attraction|museum|gallery|viewpoint|zoo|theme_park)$\"];",
            $"  nwr{around}[\"name\"][\"historic\"];",
            $"  nwr{around}[\"name\"][\"amenity\"~\"^(place_of_worship|university|college|hospital|townhall|theatre|cinema|library|marketplace)$\"];",
            $"  nwr{around}[\"name\"][\"railway\"~\"^(station|subway_entrance)$\"];",
            $"  nwr{around}[\"name\"][\"leisure\"~\"^(park|stadium|sports_centre)$\"];",
            ");",
            "out center 30;");
    }

    private static GeocodedPlace? ReadPlace(JsonElement element)
    {
        var lat = Number(element, "lat");
        var lon = Number(element, "lon");
        if (lat is null || lon is null) return null;

        // OSM answers with whichever administrative level actually carries the name, so the city
        // may arrive under any of these. Most specific first.
        var city = Text(element, "address", "city")
                   ?? Text(element, "address", "town")
                   ?? Text(element, "address", "village")
                   ?? Text(element, "address", "municipality")
                   ?? Text(element, "address", "city_district")
                   ?? Text(element, "address", "county");

        return new GeocodedPlace(
            Text(element, "display_name") ?? city ?? "",
            lat.Value, lon.Value, city,
            Text(element, "address", "country_code")?.ToUpperInvariant());
    }

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            await NominatimGate.WaitAsync(cts.Token);
            using var response = await Client().GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Nominatim answered {Status}.", (int)response.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            // A caller is on the phone. Whatever went wrong out there, the answer here is "we do
            // not know", and the tool that asked decides what to say about it.
            _logger.LogWarning(ex, "Nominatim lookup failed.");
            return null;
        }
    }

    private async Task<JsonElement?> PostOverpassAsync(string query, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Landmarks are a nicety attached to an answer the caller is waiting for, so this gets
            // the tightest budget of the three and simply drops out when it overruns.
            cts.CancelAfter(TimeSpan.FromSeconds(4));

            await OverpassGate.WaitAsync(cts.Token);
            using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("data", query)]);
            using var response = await Client().PostAsync(OverpassUrl, content, cts.Token);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "Overpass landmark lookup skipped.");
            return null;
        }
    }

    private HttpClient Client() => _factory.CreateClient(HttpClientName);

    /// <summary>The identifying User-Agent Nominatim's policy requires. Applied where the client
    /// is registered, so every request out of this process carries it.</summary>
    public static void ConfigureClient(HttpClient client, IConfiguration config)
    {
        var contact = config["Osm:Contact"];
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Frontly-AI-Receptionist/1.0" + (string.IsNullOrWhiteSpace(contact) ? "" : $" (+{contact.Trim()})"));
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        client.Timeout = TimeSpan.FromSeconds(12);
    }

    // ---------- small helpers ----------

    /// <summary>Reads through the cache, storing only what the loader says is worth keeping.
    ///
    /// A genuine "no such place" is cached like any other answer — it will not resolve on the
    /// caller's second attempt, and repeating a failed lookup is the surest way to get a
    /// deployment blocked. An answer we never received is not, because caching an outage would
    /// turn a few bad minutes into six bad hours.</summary>
    private async Task<T?> CachedAsync<T>(string key, TimeSpan ttl, Func<Task<(T? Value, bool Cacheable)>> load)
    {
        if (_cache.TryGetValue(key, out T? hit)) return hit;

        var (value, cacheable) = await load();
        if (cacheable)
            _cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, Size = 1 });
        return value;
    }

    /// <summary>Collapses whitespace and caps the length. The text arrives from a phone-call
    /// transcript and ends up in a URL, so it is bounded before it can become a very long one.</summary>
    private static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    private static string? Text(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }
        var value = current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static double? Number(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }
        return current.ValueKind switch
        {
            JsonValueKind.Number => current.GetDouble(),
            // Nominatim returns coordinates as strings; Overpass returns them as numbers.
            JsonValueKind.String when double.TryParse(current.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}

/// <summary>Holds outbound calls to one host to at most one per interval, across the whole
/// deployment. Nominatim's usage policy is an absolute rate rather than a per-user one, so this
/// has to be a single gate every request passes through — not a limit per tenant or per call.</summary>
public sealed class OutboundThrottle
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _interval;
    private DateTime _nextAllowedUtc = DateTime.MinValue;

    public OutboundThrottle(TimeSpan interval) => _interval = interval;

    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _nextAllowedUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _nextAllowedUtc = DateTime.UtcNow + _interval;
        }
        finally
        {
            _gate.Release();
        }
    }
}
