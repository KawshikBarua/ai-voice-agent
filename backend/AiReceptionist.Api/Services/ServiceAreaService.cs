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
    /// <summary>OpenStreetMap does not know this address. Almost always a mis-heard house number
    /// or street — worth one more try with the caller before anything is booked.</summary>
    NotFound,
    /// <summary>The map could not be reached. The booking goes ahead unchecked and is marked as
    /// such: refusing every caller because a free service is having a bad afternoon would be a
    /// far worse failure than the occasional trip to an address nobody vetted.</summary>
    Unverified,
    /// <summary>Inside a branch's radius, or anywhere in a city that branch covers whole.</summary>
    Covered,
    /// <summary>A real address in a city a branch works in, but further out than that branch
    /// travels. Taken as a booking, and flagged for someone to ring back and confirm.</summary>
    OutOfRange,
    /// <summary>Not in any city the business works in. Nothing is booked.</summary>
    Outside,
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
    IReadOnlyList<Landmark> Landmarks)
{
    public bool Bookable => Status is ServiceAreaStatus.NotConfigured
        or ServiceAreaStatus.Unverified or ServiceAreaStatus.Covered or ServiceAreaStatus.OutOfRange;

    /// <summary>Whether a human has to confirm this booking before anyone travels.</summary>
    public bool NeedsConfirmation => Status is ServiceAreaStatus.OutOfRange;

    /// <summary>The value stored on <see cref="Appointment.AreaStatus"/>. Null while no coverage
    /// area applies, which keeps the column empty for every tenant not using the feature.</summary>
    public string? StoredStatus => Status switch
    {
        ServiceAreaStatus.Covered => "Covered",
        ServiceAreaStatus.OutOfRange => "OutOfArea",
        ServiceAreaStatus.Unverified => "Unverified",
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

    public async Task<ServiceAreaResult> EvaluateAsync(int orgId, string? address, CancellationToken ct = default)
    {
        var spoken = (address ?? "").Trim();
        ServiceAreaResult Verdict(ServiceAreaStatus status, GeocodedPlace? place = null,
            BusinessLocation? branch = null, double? miles = null, IReadOnlyList<Landmark>? landmarks = null)
            => new(status, spoken, place, branch, miles, landmarks ?? []);

        var branches = await _locations.ListActiveAsync(orgId);
        if (branches.Count == 0 || spoken.Length == 0) return Verdict(ServiceAreaStatus.NotConfigured);

        var countries = branches.Select(b => b.CountryCode).Where(c => c.Length == 2).Distinct().ToList();
        var outcome = await _geocoding.GeocodeAsync(spoken, countries, ct);
        if (outcome.Place is not { } place)
            return Verdict(outcome.Unavailable ? ServiceAreaStatus.Unverified : ServiceAreaStatus.NotFound);

        // Landmarks are fetched once the address is known to be real, whatever the verdict turns
        // out to be: the agent reads one back to confirm it has the right street, which matters
        // most on the addresses that are about to be refused.
        var landmarks = await _geocoding.LandmarksNearAsync(place.Latitude, place.Longitude, ct);

        // Nearest first, so a business with overlapping branches sends the closest one.
        var measured = branches
            .Select(b => (Branch: b, Miles: Geo.MilesBetween(place.Latitude, place.Longitude, b.Latitude, b.Longitude)))
            .OrderBy(x => x.Miles)
            .ToList();

        foreach (var (branch, miles) in measured)
        {
            if (branch.CoversEntireCity ? InCity(branch, place) : miles <= branch.CoverageRadiusMiles)
                return Verdict(ServiceAreaStatus.Covered, place, branch, miles, landmarks);
        }

        // Out of every radius. Still in a city a branch works in? Then it is a customer the
        // business plausibly serves, and the booking is taken pending confirmation, not refused.
        var inCity = measured.FirstOrDefault(x => InCity(x.Branch, place));
        return inCity.Branch is not null
            ? Verdict(ServiceAreaStatus.OutOfRange, place, inCity.Branch, inCity.Miles, landmarks)
            : Verdict(ServiceAreaStatus.Outside, place, measured[0].Branch, measured[0].Miles, landmarks);
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
