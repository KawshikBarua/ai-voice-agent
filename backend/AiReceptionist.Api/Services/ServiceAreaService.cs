using System.Text.Json;
using System.Text.Json.Serialization;
using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;

namespace AiReceptionist.Api.Services;

/// <summary>What became of an address when it was held up against the branches' coverage areas.</summary>
public enum ServiceAreaStatus
{
    /// <summary>The organization has set no coverage areas, so there is nothing to check against
    /// and every address is taken. The state every existing tenant is in.</summary>
    NotConfigured,
    /// <summary>Nothing the caller said is on the map — not the address, not the street, not the
    /// plaza or town. Usually a mis-heard word, so the agent goes through it with them once more; if
    /// it still cannot be found it is booked anyway and flagged, and a person confirms the address
    /// before anyone travels. Turning a real customer away because a free map has not heard of their
    /// street is the worse of the two mistakes.</summary>
    NotFound,
    /// <summary>The map could not be reached. The booking goes ahead unchecked and is marked as
    /// such: refusing every caller because a free service is having a bad afternoon would be a
    /// far worse failure than the occasional trip to an address nobody vetted.</summary>
    Unverified,
    /// <summary>Inside a branch's radius, or anywhere in a city that branch covers whole — judged on
    /// the exact address where the map has it, and on the street or the area where it does not
    /// (<see cref="ServiceAreaResult.Precision"/>).</summary>
    Covered,
    /// <summary>A real address in a city a branch works in, but further out than that branch
    /// travels. Taken as a booking, and flagged for someone to ring back and confirm.</summary>
    OutOfRange,
    /// <summary>Not in any city the business works in. Nothing is booked.</summary>
    Outside,
    /// <summary>A street the business covers in more than one of its towns, and the caller named
    /// none of them. Not a refusal — a question, and the only one worth asking.</summary>
    Ambiguous,
}

/// <summary>The values stored on <see cref="Appointment.AreaStatus"/>, which the dashboard reads.</summary>
public static class AreaStatuses
{
    public const string Covered = "Covered";
    /// <summary>Taken beyond the radius, in a town a branch works in. Someone rings back to confirm.</summary>
    public const string OutOfArea = "OutOfArea";
    /// <summary>The map could not be reached, so nothing was checked.</summary>
    public const string Unverified = "Unverified";
    /// <summary>Taken on an address the map does not know at all. Someone confirms the address.</summary>
    public const string NotFound = "NotFound";
}

/// <summary>
/// The verdict on one address: what the caller said, what OpenStreetMap made of it, and which
/// branch (if any) can take it. <see cref="Bookable"/> is the single question the booking path
/// asks; everything else on here exists to explain the answer — to the agent on the call, and to
/// whoever reads the appointment afterwards.
/// </summary>
public record ServiceAreaResult(
    ServiceAreaStatus Status,
    string SpokenAddress,
    GeocodedPlace? Place,
    BusinessLocation? Branch,
    double? DistanceMiles,
    IReadOnlyList<Landmark> Landmarks,
    IReadOnlyList<string> CandidateTowns = null!)
{
    /// <summary>The towns a bare street name turned out to match, when it matched more than one.
    /// Empty for every other verdict.</summary>
    public IReadOnlyList<string> CandidateTowns { get; init; } = CandidateTowns ?? [];

    private readonly GeoPrecision? _precision;

    /// <summary>How closely <see cref="Place"/> pins down the job: the door, the street, or only the
    /// area. The map's own answer unless the search lowered it — a town or plaza found on its own is
    /// the area the job is in, however precisely the map knows that town or plaza.</summary>
    public GeoPrecision? Precision { get => _precision ?? Place?.Precision; init => _precision = value; }

    public bool Bookable => Status is ServiceAreaStatus.NotConfigured or ServiceAreaStatus.Unverified
        or ServiceAreaStatus.Covered or ServiceAreaStatus.OutOfRange or ServiceAreaStatus.NotFound;

    /// <summary>Whether a human has to confirm this booking before anyone travels.</summary>
    public bool NeedsConfirmation => Status is ServiceAreaStatus.OutOfRange or ServiceAreaStatus.NotFound;

    /// <summary>The value stored on <see cref="Appointment.AreaStatus"/>. Null while no coverage
    /// area applies, which keeps the column empty for every tenant not using the feature.</summary>
    public string? StoredStatus => Status switch
    {
        ServiceAreaStatus.Covered => AreaStatuses.Covered,
        ServiceAreaStatus.OutOfRange => AreaStatuses.OutOfArea,
        ServiceAreaStatus.Unverified => AreaStatuses.Unverified,
        ServiceAreaStatus.NotFound => AreaStatuses.NotFound,
        _ => null,
    };

    /// <summary>What OpenStreetMap returned, for <see cref="Appointment.ServiceLocationJson"/>.
    /// The caller's own wording is kept separately in <see cref="Appointment.ServiceAddress"/>:
    /// a tidied-up address is the right one to plan a route with and the wrong one to read back
    /// to the person who gave it.</summary>
    public string? ToJson() => Place is null ? null : JsonSerializer.Serialize(new StoredLocation
    {
        Spoken = SpokenAddress,
        Resolved = Place.DisplayName,
        Latitude = Math.Round(Place.Latitude, 6),
        Longitude = Math.Round(Place.Longitude, 6),
        City = Place.City,
        CountryCode = Place.CountryCode,
        Precision = Precision is { } precision and not GeoPrecision.Exact ? precision.ToString().ToLowerInvariant() : null,
        BranchId = Branch?.Id,
        BranchName = Branch?.Name,
        DistanceMiles = DistanceMiles is { } miles ? Math.Round(miles, 1) : null,
        Landmarks = Landmarks.Select(l => $"{l.Name} ({l.Meters} m)").ToList(),
    });

    /// <summary>One line naming the nearest landmark, or null when there is nothing close by.
    /// Read back to the caller as a sanity check — "just by Union Square" catches a wrong street
    /// far more reliably than repeating the postcode does.</summary>
    public string? NearestLandmark => Landmarks.Count == 0
        ? null
        : $"{Landmarks[0].Name}, about {Landmarks[0].Meters} m away";

    private sealed class StoredLocation
    {
        public string Spoken { get; set; } = "";
        public string Resolved { get; set; } = "";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string? City { get; set; }
        public string? CountryCode { get; set; }
        /// <summary>"street" or "area" when the pin is not the door, so whoever plans the route
        /// goes by the caller's words. Absent for an exact match.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Precision { get; set; }
        public int? BranchId { get; set; }
        public string? BranchName { get; set; }
        public double? DistanceMiles { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public List<string> Landmarks { get; set; } = [];
    }
}

public interface IServiceAreaService
{
    /// <summary>Holds one address up against the organization's coverage areas. Nothing the map
    /// does can throw out of here: this runs mid-call, so an outage at OpenStreetMap comes back as
    /// <see cref="ServiceAreaStatus.Unverified"/> — a verdict the agent can act on — rather than
    /// as a failed tool call the caller hears as silence.</summary>
    Task<ServiceAreaResult> EvaluateAsync(int orgId, string? address, CancellationToken ct = default);
}

/// <summary>
/// Decides whether the business will travel to an address (SRS: coverage areas).
///
/// Two things are being caught. One is an address that does not exist — a mis-heard street, a
/// house number nobody has — which OpenStreetMap answers for directly. The other is a real
/// address the business cannot reach, which is measured against each branch's centre.
///
/// The middle case is deliberate and is the one that makes this worth building: an address in a
/// city a branch works in, but beyond its radius, is still *taken* — turning away a customer
/// twenty minutes outside the line, over the phone, is a worse outcome than a booking somebody
/// rings back to confirm. Only a different city is refused.
///
/// An address the map does not know is not refused either. The map is judged on the most precise
/// piece of it that it does know — the street, the plaza, the town — and booked on that; one it
/// cannot place at all is booked and flagged for a person to confirm.
/// </summary>
public class ServiceAreaService : IServiceAreaService
{
    private readonly IBusinessLocationRepository _locations;
    private readonly IGeocodingService _geocoding;

    public ServiceAreaService(IBusinessLocationRepository locations, IGeocodingService geocoding)
    {
        _locations = locations;
        _geocoding = geocoding;
    }

    /// <summary>How many branches are worth a second, local lookup. Every one costs a round trip to
    /// a geocoder held to one request a second, and the caller is on the phone while it happens —
    /// so the pass that rescues a bare address is bounded rather than exhaustive. A business with
    /// more branches than this still gets the nationwide answer, which is what it had before.</summary>
    private const int MaxLocalRetries = 3;

    /// <summary>The most lookups spent on the pieces of an address the map could not find whole.
    /// At one lookup a second, this is how much longer a caller whose address the map has never
    /// heard of waits before the agent goes through it with them again.</summary>
    private const int MaxPieceLookups = 4;

    /// <summary>The coarsest place a piece of an address may resolve to: a town (Nominatim ranks a
    /// city 16, a county 12). A county or a state contains the branch as readily as the caller, so
    /// finding one says nothing about whether the business can reach them.</summary>
    private const int TownRank = 13;

    public async Task<ServiceAreaResult> EvaluateAsync(int orgId, string? address, CancellationToken ct = default)
    {
        var spoken = (address ?? "").Trim();
        var branches = await _locations.ListActiveAsync(orgId);
        if (branches.Count == 0 || spoken.Length == 0) return Verdict(spoken, ServiceAreaStatus.NotConfigured);

        // The gate code and the "round the back" go no further than this: they are instructions for
        // whoever turns up, and a geocoder handed them answers that the address does not exist.
        var query = SpokenAddress.ForGeocoder(spoken);
        var countries = branches.Select(b => b.CountryCode).Where(c => c.Length == 2).Distinct().ToList();

        var outcome = await _geocoding.GeocodeAsync(query, countries, within: null, ct);

        // A place was found somewhere in the country. If it is one this business covers, that is
        // the answer and no second lookup is needed.
        if (outcome.Place is { } found && Classify(found, branches) is { } hit &&
            hit.Status is ServiceAreaStatus.Covered or ServiceAreaStatus.OutOfRange)
            return await WithLandmarksAsync(Verdict(spoken, hit.Status, found, hit.Branch, hit.Miles), ct);

        // Nothing usable yet. Before anyone is turned away, consider the likeliest reasons: they gave
        // a street and a number and stopped, because they rang a local company and assumed it knew
        // the town; or they named the plaza or building along with the address, and the map files
        // those separately. Either looks exactly like "we don't come out to you".
        //
        // Only when they never named the town themselves. A caller who said "Manchester" and was
        // told Manchester is out of area has had a real answer, and searching again next door to
        // the depot would be overruling them.
        if (!SpokenAddress.Mentions(spoken, outcome.Place?.City))
        {
            var unavailable = outcome.Unavailable;

            // Found somewhere else in the country: look again near the branches. Found nowhere is
            // not worth that lookup — a box narrows a search, it cannot make the words match.
            if (outcome.Place is not null || outcome.Unavailable)
            {
                var (local, down) = await SearchNearBranchesAsync(query, branches, MaxLocalRetries, minRank: 0, ct);
                unavailable |= down;
                if (await SettleAsync(spoken, local, precision: null, ct) is { } settled) return settled;
            }

            if (!unavailable && SpokenAddress.Parse(query) is { } parts)
            {
                var (pieces, down) = await SearchPiecesAsync(spoken, query, parts, branches, countries, ct);
                if (pieces is not null) return pieces;
                unavailable |= down;
            }

            // The map stopped answering part-way. Say so rather than refusing: an outage is not a
            // verdict about where this customer lives.
            if (unavailable && outcome.Place is null)
                return Verdict(spoken, ServiceAreaStatus.Unverified);
        }

        if (outcome.Place is not { } place)
            return Verdict(spoken, outcome.Unavailable ? ServiceAreaStatus.Unverified : ServiceAreaStatus.NotFound);

        // A real address, nowhere this business goes.
        var verdict = Classify(place, branches)!;
        return await WithLandmarksAsync(Verdict(spoken, verdict.Value.Status, place, verdict.Value.Branch, verdict.Value.Miles), ct);
    }

    /// <summary>
    /// Looks for the pieces of an address the map could not find whole, and books on the closest
    /// one inside the business's area — the area rather than the door, when the door is not on the map.
    ///
    /// The town they named goes first, because it settles the most. Inside the area, the search keeps
    /// going for something closer — the number and street without the plaza in front, then without a
    /// neighbourhood the map files differently, then the plaza itself — and settles for the town when
    /// none of those is on the map. Outside the area, the town is looked up once nationwide: a caller
    /// who named Springfield, Illinois has given a real answer, and it is a refusal. Only a town the
    /// map has never heard of sends the search on without it.
    ///
    /// Apart from that one lookup everything is searched near the branches, and nothing coarser than a
    /// town counts.
    /// </summary>
    private async Task<(ServiceAreaResult? Result, bool Unavailable)> SearchPiecesAsync(
        string spoken, string query, SpokenAddress.AddressParts parts, IReadOnlyList<BusinessLocation> branches,
        IReadOnlyCollection<string> countries, CancellationToken ct)
    {
        var budget = MaxPieceLookups;
        var unavailable = false;
        var tried = new HashSet<string>(StringComparer.Ordinal) { SearchKey(query) };

        // One piece, searched near the branches: a verdict, or null when it was not worth a lookup
        // or found nothing there. A plaza or town found on its own is the area the job is in.
        async Task<ServiceAreaResult?> Near(string piece, bool isArea)
        {
            if (unavailable || budget <= 0 || piece.Length < 3 || !tried.Add(SearchKey(piece))) return null;

            var lookups = Math.Min(Math.Min(budget, MaxLocalRetries), branches.Count);
            budget -= lookups;
            var (hits, down) = await SearchNearBranchesAsync(piece, branches, lookups, TownRank, ct);
            unavailable |= down;
            return await SettleAsync(spoken, hits, isArea ? GeoPrecision.Area : null, ct);
        }

        async Task<ServiceAreaResult?> Closest(params (string Piece, bool IsArea)[] pieces)
        {
            foreach (var (piece, isArea) in pieces)
                if (await Near(piece, isArea) is { } result) return result;
            return null;
        }

        if (parts.Trail.Length > 0)
        {
            if (await Near(parts.Trail, isArea: true) is { } town)
            {
                if (town.Status == ServiceAreaStatus.Ambiguous) return (town, unavailable);
                return (await Closest((parts.NumberedInTown, false), (parts.Numbered, false), (parts.Lead, true)) ?? town,
                    unavailable);
            }
            if (unavailable) return (null, true);

            if (budget-- > 0)
            {
                var far = await _geocoding.GeocodeAsync(parts.Trail, countries, within: null, ct);
                if (far.Unavailable) return (null, true);
                if (far.Place is { Rank: >= TownRank } place && Classify(place, branches) is { } verdict)
                    return (Verdict(spoken, verdict.Status, place, verdict.Branch, verdict.Miles) with
                    {
                        Precision = GeoPrecision.Area,
                    }, false);
            }
        }

        return (await Closest((parts.Numbered, false), (parts.Lead, true)), unavailable);
    }

    /// <summary>What makes two searches the same search, case, spacing and punctuation aside.</summary>
    private static string SearchKey(string text) =>
        new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static ServiceAreaResult Verdict(string spoken, ServiceAreaStatus status, GeocodedPlace? place = null,
        BusinessLocation? branch = null, double? miles = null) =>
        new(status, spoken, place, branch, miles, []);

    /// <summary>What a search near the branches came back with, as a verdict — or null when it found
    /// nothing there. <paramref name="precision"/> overrides the map's own when the search knows better.</summary>
    private async Task<ServiceAreaResult?> SettleAsync(string spoken, List<LocalHit> hits, GeoPrecision? precision,
        CancellationToken ct)
    {
        // The same street in two towns this business works in. Nothing to refuse and nothing to
        // guess at — one question settles it.
        if (hits.Count > 1)
            return Verdict(spoken, ServiceAreaStatus.Ambiguous) with
            {
                CandidateTowns = hits.Select(x => x.Place.City ?? x.Branch.City)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            };

        if (hits.Count == 0) return null;

        var (place, status, branch, miles) = hits[0];
        var verdict = Verdict(spoken, status, place, branch, miles);
        return await WithLandmarksAsync(precision is { } p ? verdict with { Precision = p } : verdict, ct);
    }

    /// <summary>A covered address found by searching inside one branch's own patch.</summary>
    private record struct LocalHit(GeocodedPlace Place, ServiceAreaStatus Status, BusinessLocation Branch, double Miles);

    /// <summary>Looks the address up again inside the first <paramref name="lookups"/> branches'
    /// own patches, and reports what each one found. This is the pass that turns "1220 Grand" from
    /// a refusal into a booking. Anything the map ranks coarser than <paramref name="minRank"/> is
    /// ignored.</summary>
    private async Task<(List<LocalHit> Hits, bool Unavailable)> SearchNearBranchesAsync(
        string query, IReadOnlyList<BusinessLocation> branches, int lookups, int minRank, CancellationToken ct)
    {
        var hits = new List<LocalHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unavailable = false;

        foreach (var branch in branches.Take(lookups))
        {
            // A branch that covers its whole city is searched across that city; one that travels a
            // radius is searched across the radius. Either way the box is the area it actually works.
            var box = (branch.CoversEntireCity
                          ? GeoBounds.From(branch.BoundsSouth, branch.BoundsNorth, branch.BoundsWest, branch.BoundsEast)
                          : null)
                      ?? Geo.BoxAround(branch.Latitude, branch.Longitude, branch.CoverageRadiusMiles);

            var outcome = await _geocoding.GeocodeAsync(query, [branch.CountryCode], box, ct);
            if (outcome.Unavailable) unavailable = true;
            if (outcome.Place is not { } place || place.Rank < minRank) continue;

            // Judged against every branch, not just the one whose box found it: a business with two
            // depots twenty miles apart will often have the nearer one cover what the other found.
            if (Classify(place, branches) is not { } verdict ||
                verdict.Status is not (ServiceAreaStatus.Covered or ServiceAreaStatus.OutOfRange))
                continue;

            // Two boxes overlapping onto one address is one answer, not two.
            var key = $"{place.Latitude:F5}|{place.Longitude:F5}";
            if (seen.Add(key))
                hits.Add(new LocalHit(place, verdict.Status, verdict.Branch, verdict.Miles));
        }

        return (hits, unavailable);
    }

    /// <summary>Which branch, if any, takes this address — and how far out it is.</summary>
    private static (ServiceAreaStatus Status, BusinessLocation Branch, double Miles)? Classify(
        GeocodedPlace place, IReadOnlyList<BusinessLocation> branches)
    {
        if (branches.Count == 0) return null;

        // Nearest first, so a business with overlapping branches sends the closest one.
        var measured = branches
            .Select(b => (Branch: b, Miles: Geo.MilesBetween(place.Latitude, place.Longitude, b.Latitude, b.Longitude)))
            .OrderBy(x => x.Miles)
            .ToList();

        foreach (var (branch, miles) in measured)
        {
            if (branch.CoversEntireCity ? InCity(branch, place) : miles <= branch.CoverageRadiusMiles)
                return (ServiceAreaStatus.Covered, branch, miles);
        }

        // Out of every radius. Still in a city a branch works in? Then it is a customer the
        // business plausibly serves, and the booking is taken pending confirmation, not refused.
        var inCity = measured.FirstOrDefault(x => InCity(x.Branch, place));
        return inCity.Branch is not null
            ? (ServiceAreaStatus.OutOfRange, inCity.Branch, inCity.Miles)
            : (ServiceAreaStatus.Outside, measured[0].Branch, measured[0].Miles);
    }

    /// <summary>Attaches the landmarks beside a resolved address. Best-effort and never fatal: the
    /// agent uses one to check it heard the street right, and an address without one is simply
    /// confirmed the ordinary way. Only beside an exact match: next to the middle of a street or a
    /// town it names somewhere the caller is not, and invites a correction to an address that was right.</summary>
    private async Task<ServiceAreaResult> WithLandmarksAsync(ServiceAreaResult result, CancellationToken ct)
    {
        if (result.Place is not { } place || result.Precision != GeoPrecision.Exact) return result;
        return result with { Landmarks = await _geocoding.LandmarksNearAsync(place.Latitude, place.Longitude, ct) };
    }

    /// <summary>Whether an address sits in the branch's city.
    ///
    /// The city's own rectangle decides it wherever one was stored, because the names cannot: an
    /// address on Downing Street calls its city "City of Westminster", the search that found the
    /// branch called it "Greater London", and the owner typed "London". Comparing those three
    /// strings would refuse most of London to a business covering all of it; one rectangle holds
    /// all three. Names are only consulted for a branch saved before boundaries were stored.</summary>
    private static bool InCity(BusinessLocation branch, GeocodedPlace place) =>
        GeoBounds.From(branch.BoundsSouth, branch.BoundsNorth, branch.BoundsWest, branch.BoundsEast)
            is { } bounds
            ? bounds.Contains(place.Latitude, place.Longitude)
            : Geo.SameCity(branch.City, place.City);
}
