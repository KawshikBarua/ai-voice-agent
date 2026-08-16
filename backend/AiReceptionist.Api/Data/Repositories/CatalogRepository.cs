using AiReceptionist.Api.Common;
using AiReceptionist.Api.Domain;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

public interface ICatalogRepository
{
    Task<PagedResult<Service>> ListServicesAsync(int orgId, int page, int pageSize);
    Task<int> CreateServiceAsync(Service s);
    Task UpdateServiceAsync(Service s);
    Task DeleteServiceAsync(int orgId, int id);

    Task<PagedResult<Product>> ListProductsAsync(int orgId, string? search, int page, int pageSize);
    Task<int> CreateProductAsync(Product p);
    Task UpdateProductAsync(Product p);
    Task DeleteProductAsync(int orgId, int id);
}

public class CatalogRepository : ICatalogRepository
{
    private readonly IDbConnectionFactory _db;
    public CatalogRepository(IDbConnectionFactory db) => _db = db;

    public async Task<PagedResult<Service>> ListServicesAsync(int orgId, int page, int pageSize)
    {
        using var conn = _db.Create();
        var p = new { orgId, skip = (page - 1) * pageSize, take = pageSize };
        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Services WHERE OrganizationId=@orgId AND IsDeleted=0", p);
        var items = await conn.QueryAsync<Service>(
            "SELECT * FROM Services WHERE OrganizationId=@orgId AND IsDeleted=0 ORDER BY Name OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY", p);
        return new PagedResult<Service> { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
    }

    public async Task<int> CreateServiceAsync(Service s)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO Services (OrganizationId, Name, Description, DurationMinutes, MinPrice, MaxPrice, IsEmergency, IsAvailable)
            OUTPUT INSERTED.Id
            VALUES (@OrganizationId, @Name, @Description, @DurationMinutes, @MinPrice, @MaxPrice, @IsEmergency, @IsAvailable)", s);
    }

    public async Task UpdateServiceAsync(Service s)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE Services SET Name=@Name, Description=@Description, DurationMinutes=@DurationMinutes,
                MinPrice=@MinPrice, MaxPrice=@MaxPrice, IsEmergency=@IsEmergency, IsAvailable=@IsAvailable
            WHERE OrganizationId=@OrganizationId AND Id=@Id AND IsDeleted=0", s);
    }

    public async Task DeleteServiceAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("UPDATE Services SET IsDeleted=1 WHERE OrganizationId=@orgId AND Id=@id", new { orgId, id });
    }

    public async Task<PagedResult<Product>> ListProductsAsync(int orgId, string? search, int page, int pageSize)
    {
        using var conn = _db.Create();
        var where = "OrganizationId=@orgId AND IsDeleted=0" +
                    (string.IsNullOrWhiteSpace(search) ? "" : " AND (Name LIKE @s OR Sku LIKE @s OR Category LIKE @s)");
        var p = new { orgId, s = $"%{search}%", skip = (page - 1) * pageSize, take = pageSize };
        var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM Products WHERE {where}", p);
        var items = await conn.QueryAsync<Product>(
            $"SELECT * FROM Products WHERE {where} ORDER BY Name OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY", p);
        return new PagedResult<Product> { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
    }

    public async Task<int> CreateProductAsync(Product p)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO Products (OrganizationId, Name, Sku, Description, Category, Price, Quantity, IsAvailable, IsActive)
            OUTPUT INSERTED.Id
            VALUES (@OrganizationId, @Name, @Sku, @Description, @Category, @Price, @Quantity, @IsAvailable, @IsActive)", p);
    }

    public async Task UpdateProductAsync(Product p)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE Products SET Name=@Name, Sku=@Sku, Description=@Description, Category=@Category,
                Price=@Price, Quantity=@Quantity, IsAvailable=@IsAvailable, IsActive=@IsActive
            WHERE OrganizationId=@OrganizationId AND Id=@Id AND IsDeleted=0", p);
    }

    public async Task DeleteProductAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("UPDATE Products SET IsDeleted=1 WHERE OrganizationId=@orgId AND Id=@id", new { orgId, id });
    }
}
