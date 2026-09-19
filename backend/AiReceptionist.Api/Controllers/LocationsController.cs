using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

/// <summary>
/// The branches a business works out of, and how far each one travels.
///
/// These are what an address given on a call is checked against before anything is booked, so
/// the coordinates behind them are resolved here on the server and never accepted from the
/// browser: a client that could set a branch's centre could put any address inside it.
///
/// An organization with no branches has no coverage rules, and every screen and every call
/// behaves exactly as it did before this existed.
/// </summary>
[ApiController]
[Route("api/v1/locations")]
[Authorize]
public class LocationsController : ControllerBase
{
    private const double MaxRadiusMiles = 200;

    private readonly IBusinessLocationRepository _locations;
    private readonly IGeocodingService _geocoding;
    private readonly ITenantProvider _tenant;
    private readonly IAuditRepository _audit;
    private readonly IRetellSyncQueue _retellSync;

    public LocationsController(IBusinessLocationRepository locations, IGeocodingService geocoding,
        ITenantProvider tenant, IAuditRepository audit, IRetellSyncQueue retellSync)
    {
        _locations = locations;
        _geocoding = geocoding;
        _tenant = tenant;
        _audit = audit;
        _retellSync = retellSync;
    }

    [HttpGet]
    public async Task<IActionResult> List() =>
        Ok(ApiResponse<IEnumerable<BusinessLocation>>.Ok(await _locations.ListAsync(_tenant.OrganizationId)));

    /// <summary>Every country the branch picker offers. Fixed data — the client caches it for the
    /// session rather than asking again.</summary>
    [HttpGet("countries")]
    public IActionResult ListCountries() => Ok(ApiResponse<IReadOnlyList<Country>>.Ok(Countries.All));

    /// <summary>Cities in one country matching what has been typed. Restricted to management
    /// because it reaches out to OpenStreetMap on the organization's behalf, and an endpoint that
    /// does that for anyone who asks is an open proxy with our name on the requests.</summary>
    [HttpGet("cities")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> SearchCities(string countryCode, string q, CancellationToken ct)
    {
        if (Countries.NameFor(countryCode) is null)
            return BadRequest(ApiResponse<object>.Fail("Choose a country first."));

        var matches = await _geocoding.SearchCitiesAsync(countryCode, q ?? "", ct);
        return Ok(ApiResponse<IReadOnlyList<CitySuggestion>>.Ok(matches));
    }

    [HttpPost]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Create(BusinessLocation location, CancellationToken ct)
    {
        var (prepared, problem) = await PrepareAsync(location, ct);
        if (problem is not null) return BadRequest(ApiResponse<object>.Fail(problem));

        prepared!.Id = await _locations.CreateAsync(prepared);
        await AnnounceAsync("BusinessLocationAdded", $"{prepared.City}, {prepared.CountryName}");
        return Ok(ApiResponse<BusinessLocation>.Ok(prepared, $"{prepared.Name} added."));
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Update(int id, BusinessLocation location, CancellationToken ct)
    {
        var (prepared, problem) = await PrepareAsync(location, ct);
        if (problem is not null) return BadRequest(ApiResponse<object>.Fail(problem));

        prepared!.Id = id;
        if (!await _locations.UpdateAsync(prepared))
            return NotFound(ApiResponse<object>.Fail("That location no longer exists."));

        await AnnounceAsync("BusinessLocationUpdated", $"{prepared.City}, {prepared.CountryName}");
        return Ok(ApiResponse<BusinessLocation>.Ok(prepared, $"{prepared.Name} updated."));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await _locations.DeleteAsync(_tenant.OrganizationId, id))
            return NotFound(ApiResponse<object>.Fail("That location no longer exists."));

        await AnnounceAsync("BusinessLocationRemoved", $"Id={id}");
        return Ok(ApiResponse<object>.Ok(new { }, "Location removed."));
    }

    /// <summary>Validates what was posted and resolves the branch's centre from its city. Only
    /// the fields listed here survive: everything else on the entity is either ours to set (the
    /// tenant id, the timestamps) or ours to derive (the coordinates).</summary>
    private async Task<(BusinessLocation?, string?)> PrepareAsync(BusinessLocation input, CancellationToken ct)
    {
        var countryName = Countries.NameFor(input.CountryCode);
        if (countryName is null) return (null, "Choose a country.");

        var city = (input.City ?? "").Trim();
        if (city.Length is 0 or > 150) return (null, "Choose a city.");

        // Resolved through the same search the picker used, so the usual path is a cache hit and
        // saving is instant — and a city that does not exist in the chosen country is caught here
        // rather than at the first call that depends on it.
        var matches = await _geocoding.SearchCitiesAsync(input.CountryCode, city, ct);
        var match = matches.FirstOrDefault(m => Geo.SameCity(m.City, city)) ?? matches.FirstOrDefault();
        if (match is null)
            return (null, $"We could not find '{city}' in {countryName}. Check the spelling, or pick from the list.");

        var name = (input.Name ?? "").Trim();
        return (new BusinessLocation
        {
            OrganizationId = _tenant.OrganizationId,
            Name = name.Length is > 0 and <= 150 ? name : match.City,
            CountryCode = input.CountryCode.ToUpperInvariant(),
            CountryName = countryName,
            City = match.City,
            Latitude = match.Latitude,
            Longitude = match.Longitude,
            BoundsSouth = match.Bounds?.South,
            BoundsNorth = match.Bounds?.North,
            BoundsWest = match.Bounds?.West,
            BoundsEast = match.Bounds?.East,
            // A radius of nothing would silently cover nothing at all, so the floor is one mile.
            CoverageRadiusMiles = Math.Clamp(input.CoverageRadiusMiles, 1, MaxRadiusMiles),
            CoversEntireCity = input.CoversEntireCity,
            IsActive = input.IsActive,
        }, null);
    }

    private async Task AnnounceAsync(string action, string detail)
    {
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, action, detail);
        // The prompt states where the business works, so the agent has to be told it changed.
        _retellSync.Enqueue(_tenant.OrganizationId);
    }
}
