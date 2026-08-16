using AiReceptionist.Api.Common;
using AiReceptionist.Api.Domain;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

public interface IAiToolsRepository
{
    Task<Service?> FindServiceByNameAsync(int orgId, string name);
    Task<IEnumerable<Appointment>> UpcomingAppointmentsAsync(int orgId, int customerId,
        DateTime? fromUtc = null, DateTime? toUtc = null, string? serviceName = null, int take = 5);
    Task<IEnumerable<Appointment>> AppointmentsBetweenAsync(int orgId, DateTime fromUtc, DateTime toUtc);
}

public class AiToolsRepository : IAiToolsRepository
{
    private readonly IDbConnectionFactory _db;
    public AiToolsRepository(IDbConnectionFactory db) => _db = db;

    public async Task<Service?> FindServiceByNameAsync(int orgId, string name)
    {
        using var conn = _db.Create();
        return await conn.QueryFirstOrDefaultAsync<Service>(@"
            SELECT TOP 1 * FROM Services
            WHERE OrganizationId=@orgId AND IsDeleted=0 AND IsAvailable=1 AND Name LIKE @pattern
            ORDER BY LEN(Name)", new { orgId, pattern = $"%{SqlUtil.EscapeLike(name.Trim())}%" });
    }

    public async Task<IEnumerable<Appointment>> UpcomingAppointmentsAsync(int orgId, int customerId,
        DateTime? fromUtc = null, DateTime? toUtc = null, string? serviceName = null, int take = 5)
    {
        using var conn = _db.Create();
        var sql = @"
            SELECT TOP (@take) a.*, c.Name AS CustomerName, c.Phone AS CustomerPhone, s.Name AS ServiceName
            FROM Appointments a
            JOIN Customers c ON c.Id = a.CustomerId
            LEFT JOIN Services s ON s.Id = a.ServiceId
            WHERE a.OrganizationId=@orgId AND a.CustomerId=@customerId AND a.IsDeleted=0
              AND a.Status IN ('Scheduled','Confirmed') AND a.StartAt >= GETUTCDATE()";
        if (fromUtc.HasValue) sql += " AND a.StartAt >= @fromUtc";
        if (toUtc.HasValue) sql += " AND a.StartAt < @toUtc";
        if (!string.IsNullOrWhiteSpace(serviceName)) sql += " AND s.Name LIKE @servicePattern";
        sql += " ORDER BY a.StartAt";

        return await conn.QueryAsync<Appointment>(sql, new
        {
            orgId, customerId, fromUtc, toUtc, take,
            servicePattern = serviceName is null ? null : $"%{SqlUtil.EscapeLike(serviceName.Trim())}%",
        });
    }

    public async Task<IEnumerable<Appointment>> AppointmentsBetweenAsync(int orgId, DateTime fromUtc, DateTime toUtc)
    {
        using var conn = _db.Create();
        return await conn.QueryAsync<Appointment>(@"
            SELECT * FROM Appointments
            WHERE OrganizationId=@orgId AND IsDeleted=0 AND Status NOT IN ('Cancelled','Missed')
              AND StartAt < @toUtc AND EndAt > @fromUtc", new { orgId, fromUtc, toUtc });
    }
}
