using AiReceptionist.Api.Domain;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

public interface IBusinessLocationRepository
{
    Task<IEnumerable<BusinessLocation>> ListAsync(int orgId);
    /// <summary>Only the branches that can actually cover an address. Read on the booking path,
    /// so it is deliberately the narrowest query: no joins, one index, and an empty result is the
    /// answer for every organization that has never configured a coverage area.</summary>
    Task<IReadOnlyList<BusinessLocation>> ListActiveAsync(int orgId);
    Task<int> CreateAsync(BusinessLocation location);
    Task<bool> UpdateAsync(BusinessLocation location);
    Task<bool> DeleteAsync(int orgId, int id);
}

public class BusinessLocationRepository : IBusinessLocationRepository
{
    private readonly IDbConnectionFactory _db;
    public BusinessLocationRepository(IDbConnectionFactory db) => _db = db;

    public async Task<IEnumerable<BusinessLocation>> ListAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.QueryAsync<BusinessLocation>(
            @"SELECT * FROM BusinessLocations
              WHERE OrganizationId=@orgId AND IsDeleted=0
              ORDER BY IsActive DESC, CountryName, City", new { orgId });
    }

    public async Task<IReadOnlyList<BusinessLocation>> ListActiveAsync(int orgId)
    {
        using var conn = _db.Create();
        var rows = await conn.QueryAsync<BusinessLocation>(
            "SELECT * FROM BusinessLocations WHERE OrganizationId=@orgId AND IsDeleted=0 AND IsActive=1",
            new { orgId });
        return rows.AsList();
    }

    public async Task<int> CreateAsync(BusinessLocation l)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO BusinessLocations (OrganizationId, Name, CountryCode, CountryName, City,
                Latitude, Longitude, CoverageRadiusMiles, CoversEntireCity, IsActive,
                BoundsSouth, BoundsNorth, BoundsWest, BoundsEast)
            OUTPUT INSERTED.Id
            VALUES (@OrganizationId, @Name, @CountryCode, @CountryName, @City,
                @Latitude, @Longitude, @CoverageRadiusMiles, @CoversEntireCity, @IsActive,
                @BoundsSouth, @BoundsNorth, @BoundsWest, @BoundsEast)", l);
    }

    public async Task<bool> UpdateAsync(BusinessLocation l)
    {
        using var conn = _db.Create();
        var rows = await conn.ExecuteAsync(@"
            UPDATE BusinessLocations
            SET Name=@Name, CountryCode=@CountryCode, CountryName=@CountryName, City=@City,
                Latitude=@Latitude, Longitude=@Longitude, CoverageRadiusMiles=@CoverageRadiusMiles,
                CoversEntireCity=@CoversEntireCity, IsActive=@IsActive,
                BoundsSouth=@BoundsSouth, BoundsNorth=@BoundsNorth, BoundsWest=@BoundsWest, BoundsEast=@BoundsEast,
                ModifiedAt=GETUTCDATE()
            WHERE OrganizationId=@OrganizationId AND Id=@Id AND IsDeleted=0", l);
        return rows > 0;
    }

    // Soft delete, like everything else here: appointments already taken point at this branch by
    // id in their stored location, and the row is what makes that readable afterwards.
    public async Task<bool> DeleteAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        var rows = await conn.ExecuteAsync(
            @"UPDATE BusinessLocations SET IsDeleted=1, IsActive=0, ModifiedAt=GETUTCDATE()
              WHERE OrganizationId=@orgId AND Id=@id AND IsDeleted=0", new { orgId, id });
        return rows > 0;
    }
}
