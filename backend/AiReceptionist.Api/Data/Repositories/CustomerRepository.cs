using AiReceptionist.Api.Common;
using AiReceptionist.Api.Domain;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

public interface ICustomerRepository
{
    Task<PagedResult<Customer>> ListAsync(int orgId, string? search, int page, int pageSize);
    Task<Customer?> GetAsync(int orgId, int id);
    Task<Customer?> FindReturningAsync(int orgId, string? phone, string? email, string? name);
    /// <summary>Everyone on record under a phone number, most recently seen first. A household or
    /// a business shares one number between several people, so a number alone does not say who
    /// is calling; the live-call tools pick among these by name.</summary>
    Task<List<Customer>> ListByPhoneAsync(int orgId, string? phone);
    Task<int> CreateAsync(Customer customer);
    Task UpdateAsync(Customer customer);
    Task SoftDeleteAsync(int orgId, int id);
    Task<IEnumerable<TimelineEvent>> TimelineAsync(int orgId, int customerId);
    Task AddTimelineEventAsync(TimelineEvent evt);
}

public class CustomerRepository : ICustomerRepository
{
    private readonly IDbConnectionFactory _db;
    public CustomerRepository(IDbConnectionFactory db) => _db = db;

    public async Task<PagedResult<Customer>> ListAsync(int orgId, string? search, int page, int pageSize)
    {
        using var conn = _db.Create();
        var where = "OrganizationId = @orgId AND IsDeleted = 0" +
                    (string.IsNullOrWhiteSpace(search) ? "" : " AND (Name LIKE @s OR Phone LIKE @s OR Email LIKE @s)");
        var p = new { orgId, s = $"%{search}%", skip = (page - 1) * pageSize, take = pageSize };

        var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM Customers WHERE {where}", p);
        var items = await conn.QueryAsync<Customer>(
            $"SELECT * FROM Customers WHERE {where} ORDER BY LastVisit DESC, Id DESC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY", p);

        return new PagedResult<Customer> { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
    }

    public async Task<Customer?> GetAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<Customer>(
            "SELECT * FROM Customers WHERE OrganizationId = @orgId AND Id = @id AND IsDeleted = 0", new { orgId, id });
    }

    // Returning-customer detection order (SRS §7): phone → email → name.
    // Phone comparison is on the canonical format (PhoneUtil) so "+1 555-010-0777",
    // "+15550100777" and "555 010 0777" all resolve to the same customer.
    public async Task<Customer?> FindReturningAsync(int orgId, string? phone, string? email, string? name)
    {
        using var conn = _db.Create();
        const string baseSql = "SELECT TOP 1 * FROM Customers WHERE OrganizationId = @orgId AND IsDeleted = 0 AND ";
        Customer? found = null;
        if (!string.IsNullOrWhiteSpace(phone))
        {
            var normalized = PhoneUtil.Normalize(phone);
            found = await conn.QueryFirstOrDefaultAsync<Customer>(baseSql + "Phone = @normalized",
                new { orgId, normalized });
            // Fallback: match on trailing digits (handles missing country code either way)
            if (found is null && normalized.TrimStart('+').Length >= 7)
                found = await conn.QueryFirstOrDefaultAsync<Customer>(
                    baseSql + "REPLACE(Phone,'+','') LIKE '%' + @tail",
                    new { orgId, tail = Tail(normalized) });
        }
        if (found is null && !string.IsNullOrWhiteSpace(email))
            found = await conn.QueryFirstOrDefaultAsync<Customer>(baseSql + "Email = @email", new { orgId, email });
        if (found is null && !string.IsNullOrWhiteSpace(name))
            found = await conn.QueryFirstOrDefaultAsync<Customer>(baseSql + "Name = @name", new { orgId, name });
        return found;

        static string Tail(string normalized)
        {
            var digits = normalized.TrimStart('+');
            return digits.Length <= 9 ? digits : digits[^9..];
        }
    }

    public async Task<List<Customer>> ListByPhoneAsync(int orgId, string? phone)
    {
        var normalized = PhoneUtil.Normalize(phone);
        if (normalized.TrimStart('+').Length == 0) return [];

        // The same two readings FindReturningAsync makes — exact, then the trailing digits so a
        // missing country code on either side still matches — but every row, not the first one.
        var digits = normalized.TrimStart('+');
        var tail = digits.Length <= 9 ? digits : digits[^9..];

        using var conn = _db.Create();
        var rows = await conn.QueryAsync<Customer>(@"
            SELECT TOP 10 * FROM Customers
            WHERE OrganizationId = @orgId AND IsDeleted = 0
              AND (Phone = @normalized OR (LEN(@tail) >= 7 AND REPLACE(Phone,'+','') LIKE '%' + @tail))
            ORDER BY CASE WHEN Phone = @normalized THEN 0 ELSE 1 END, LastVisit DESC, Id DESC",
            new { orgId, normalized, tail });
        return rows.AsList();
    }

    public async Task<int> CreateAsync(Customer c)
    {
        c.Phone = PhoneUtil.Normalize(c.Phone);
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO Customers (OrganizationId, Name, Phone, Email, Address, Notes, FirstVisit, TotalVisits)
            OUTPUT INSERTED.Id
            VALUES (@OrganizationId, @Name, @Phone, @Email, @Address, @Notes, GETUTCDATE(), 0)", c);
    }

    public async Task UpdateAsync(Customer c)
    {
        c.Phone = PhoneUtil.Normalize(c.Phone);
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE Customers SET Name=@Name, Phone=@Phone, Email=@Email, Address=@Address, Notes=@Notes
            WHERE OrganizationId=@OrganizationId AND Id=@Id AND IsDeleted=0", c);
    }

    public async Task SoftDeleteAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("UPDATE Customers SET IsDeleted=1 WHERE OrganizationId=@orgId AND Id=@id",
            new { orgId, id });
    }

    public async Task<IEnumerable<TimelineEvent>> TimelineAsync(int orgId, int customerId)
    {
        using var conn = _db.Create();
        return await conn.QueryAsync<TimelineEvent>(
            "SELECT * FROM TimelineEvents WHERE OrganizationId=@orgId AND CustomerId=@customerId ORDER BY OccurredAt DESC",
            new { orgId, customerId });
    }

    public async Task AddTimelineEventAsync(TimelineEvent evt)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            INSERT INTO TimelineEvents (OrganizationId, CustomerId, EventType, Notes, Source, UserId)
            VALUES (@OrganizationId, @CustomerId, @EventType, @Notes, @Source, @UserId)", evt);
    }
}
