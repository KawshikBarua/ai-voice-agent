using AiReceptionist.Api.Common;
using AiReceptionist.Api.Domain;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

public interface IEmployeeRepository
{
    Task<IEnumerable<Employee>> ListAsync(int orgId);
    Task<Employee?> GetAsync(int orgId, int id);
    Task<int> CreateAsync(Employee employee);
    Task<bool> UpdateAsync(Employee employee);
    /// <summary>Soft-deletes the employee. Returns false when they still have appointments to
    /// come — those would be left with nobody to keep them.</summary>
    Task<bool> DeleteAsync(int orgId, int id);

    Task<IEnumerable<EmployeeTimeOff>> ListTimeOffAsync(int orgId, DateTime fromLocalDate);
    Task<int> AddTimeOffAsync(EmployeeTimeOff timeOff);
    Task DeleteTimeOffAsync(int orgId, int id);

    /// <summary>The roster as the booking rules need it: everyone active, plus the leave that
    /// touches the local dates being looked at. One round trip for a whole day of slots.</summary>
    Task<StaffRoster> LoadRosterAsync(int orgId, DateTime fromLocalDate, DateTime toLocalDate);
}

public class EmployeeRepository : IEmployeeRepository
{
    private readonly IDbConnectionFactory _db;
    public EmployeeRepository(IDbConnectionFactory db) => _db = db;

    // UpcomingAppointments is what the team screen warns on before someone is removed, so it
    // counts the same statuses the availability rules treat as occupying a slot.
    private const string SelectSql = @"
        SELECT e.*,
               (SELECT COUNT(*) FROM Appointments a
                WHERE a.OrganizationId = e.OrganizationId AND a.EmployeeId = e.Id
                  AND a.IsDeleted = 0 AND a.Status NOT IN ('Cancelled','Missed')
                  AND a.StartAt >= GETUTCDATE()) AS UpcomingAppointments
        FROM Employees e";

    public async Task<IEnumerable<Employee>> ListAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.QueryAsync<Employee>(
            $"{SelectSql} WHERE e.OrganizationId=@orgId AND e.IsDeleted=0 ORDER BY e.IsActive DESC, e.Name",
            new { orgId });
    }

    public async Task<Employee?> GetAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<Employee>(
            $"{SelectSql} WHERE e.OrganizationId=@orgId AND e.Id=@id AND e.IsDeleted=0", new { orgId, id });
    }

    public async Task<int> CreateAsync(Employee e)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO Employees (OrganizationId, Name, JobTitle, Phone, Email, WorkingHoursJson, IsActive)
            OUTPUT INSERTED.Id
            VALUES (@OrganizationId, @Name, @JobTitle, @Phone, @Email, @WorkingHoursJson, @IsActive)", e);
    }

    public async Task<bool> UpdateAsync(Employee e)
    {
        using var conn = _db.Create();
        var rows = await conn.ExecuteAsync(@"
            UPDATE Employees SET Name=@Name, JobTitle=@JobTitle, Phone=@Phone, Email=@Email,
                WorkingHoursJson=@WorkingHoursJson, IsActive=@IsActive, ModifiedAt=GETUTCDATE()
            WHERE OrganizationId=@OrganizationId AND Id=@Id AND IsDeleted=0", e);
        return rows > 0;
    }

    // Removing someone who is still due to see callers would leave those appointments assigned to
    // a person no longer on the roster, and the slot would look free again. The refusal points the
    // owner at deactivating instead, which keeps the bookings and stops any new ones.
    public async Task<bool> DeleteAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        var rows = await conn.ExecuteAsync(@"
            UPDATE Employees SET IsDeleted=1, IsActive=0, ModifiedAt=GETUTCDATE()
            WHERE OrganizationId=@orgId AND Id=@id AND IsDeleted=0
              AND NOT EXISTS (SELECT 1 FROM Appointments a
                              WHERE a.OrganizationId=@orgId AND a.EmployeeId=@id AND a.IsDeleted=0
                                AND a.Status NOT IN ('Cancelled','Missed') AND a.StartAt >= GETUTCDATE())",
            new { orgId, id });
        return rows > 0;
    }

    public async Task<IEnumerable<EmployeeTimeOff>> ListTimeOffAsync(int orgId, DateTime fromLocalDate)
    {
        using var conn = _db.Create();
        return await conn.QueryAsync<EmployeeTimeOff>(@"
            SELECT t.*, e.Name AS EmployeeName
            FROM EmployeeTimeOff t
            JOIN Employees e ON e.Id = t.EmployeeId
            WHERE t.OrganizationId=@orgId AND t.IsDeleted=0 AND e.IsDeleted=0 AND t.EndDate >= @from
            ORDER BY t.StartDate, e.Name", new { orgId, from = fromLocalDate.Date });
    }

    public async Task<int> AddTimeOffAsync(EmployeeTimeOff t)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO EmployeeTimeOff (OrganizationId, EmployeeId, StartDate, EndDate, Reason)
            OUTPUT INSERTED.Id
            VALUES (@OrganizationId, @EmployeeId, @StartDate, @EndDate, @Reason)",
            new { t.OrganizationId, t.EmployeeId, StartDate = t.StartDate.Date, EndDate = t.EndDate.Date, t.Reason });
    }

    public async Task DeleteTimeOffAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(
            "UPDATE EmployeeTimeOff SET IsDeleted=1 WHERE OrganizationId=@orgId AND Id=@id",
            new { orgId, id });
    }

    public async Task<StaffRoster> LoadRosterAsync(int orgId, DateTime fromLocalDate, DateTime toLocalDate)
    {
        using var conn = _db.Create();
        using var results = await conn.QueryMultipleAsync(@"
            SELECT * FROM Employees
            WHERE OrganizationId=@orgId AND IsDeleted=0 AND IsActive=1;

            SELECT * FROM EmployeeTimeOff
            WHERE OrganizationId=@orgId AND IsDeleted=0
              AND StartDate <= @to AND EndDate >= @from;",
            new { orgId, from = fromLocalDate.Date, to = toLocalDate.Date });

        var employees = await results.ReadAsync<Employee>();
        var timeOff = await results.ReadAsync<EmployeeTimeOff>();
        return new StaffRoster(employees, timeOff);
    }
}
