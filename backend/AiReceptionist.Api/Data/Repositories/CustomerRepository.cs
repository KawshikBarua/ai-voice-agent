using AiReceptionist.Api.Common;
using AiReceptionist.Api.Domain;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

public interface ICustomerRepository
{
    Task<CursorPage<Customer>> ListAsync(int orgId, string? search, KeysetCursor? after, int pageSize);
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

    // Keyset paging: each page seeks straight to where the last one ended, so page 20,000 costs
    // the same as page 1. OFFSET had to read and discard every earlier row, and the COUNT(*)
    // beside it scanned the whole tenant on every request.
    //
    // The order is LastVisit DESC, Id DESC, and LastVisit is nullable. Customers who have never
    // visited sort last, so the list is read as two ordered runs — visited, then never visited —
    // each of which is a plain range on IX_Customers_Org_LastVisit.
    public async Task<CursorPage<Customer>> ListAsync(int orgId, string? search, KeysetCursor? after, int pageSize)
    {
        var filter = "OrganizationId = @orgId AND IsDeleted = 0" +
                     (string.IsNullOrWhiteSpace(search) ? "" : " AND (Name LIKE @s OR Phone LIKE @s OR Email LIKE @s)");

        var runs = new List<string>(2);
        if (after is null)
            runs.Add(Run("LastVisit IS NOT NULL", "LastVisit DESC, Id DESC"));
        else if (after.Value.At is not null)
            // "<= AND (< OR)" rather than "< OR (= AND <)": the leading comparison is one the
            // index can seek on, where a top-level OR would fall back to scanning.
            runs.Add(Run("LastVisit <= @lv AND (LastVisit < @lv OR Id < @id)", "LastVisit DESC, Id DESC"));
        runs.Add(Run("LastVisit IS NULL" + (after is { At: null } ? " AND Id < @id" : ""), "Id DESC"));

        var p = new DynamicParameters();
        p.Add("orgId", orgId);
        p.Add("s", $"%{SqlUtil.EscapeLike(search ?? "")}%");
        // One more than asked for: its presence is what says another page exists.
        p.Add("take", pageSize + 1);
        p.Add("id", after?.Id, System.Data.DbType.Int32);
        // Typed explicitly: left to inference this is sent as DATETIME, whose 3ms rounding would
        // stop the cursor comparing equal to the row it was taken from.
        p.Add("lv", after?.At, System.Data.DbType.DateTime2);

        using var conn = _db.Create();
        var rows = (await conn.QueryAsync<Customer>(
            $"SELECT TOP (@take) * FROM ({string.Join(" UNION ALL ", runs)}) c ORDER BY LastVisit DESC, Id DESC", p)).AsList();

        var items = rows.Take(pageSize).ToList();
        return new CursorPage<Customer>
        {
            Items = items,
            NextCursor = rows.Count > pageSize ? new KeysetCursor(items[^1].LastVisit, items[^1].Id).ToString() : null,
        };

        string Run(string range, string order) =>
            $"SELECT * FROM (SELECT TOP (@take) * FROM Customers WHERE {filter} AND {range} ORDER BY {order}) r";
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
