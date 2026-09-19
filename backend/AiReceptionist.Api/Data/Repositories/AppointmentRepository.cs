using AiReceptionist.Api.Common;
using AiReceptionist.Api.Domain;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

/// <summary>
/// Who may take one slot, worked out from the roster (see <see cref="StaffRoster"/>) just before
/// the write. The repository still decides which of them actually gets it, because only the
/// database can tell who is free without racing a second call booking the same minute.
///
/// <c>OnDutyIds</c> is everyone working that slot, and is what capacity is measured against.
/// <c>CandidateIds</c> is who this particular booking may go to, in order of preference: the whole
/// on-duty set when the AI is free to choose, a single id when one person was asked for, and the
/// current holder first when an appointment is being moved.
/// </summary>
public sealed record SlotAssignment(IReadOnlyList<int> OnDutyIds, IReadOnlyList<int> CandidateIds)
{
    public static SlotAssignment Any(IEnumerable<int> onDutyIds)
    {
        var ids = onDutyIds.ToList();
        return new SlotAssignment(ids, ids);
    }

    /// <summary>The same slot, but this booking must go to one named person — who is still only
    /// bookable if they are on duty and free.</summary>
    public SlotAssignment Only(int employeeId) => this with { CandidateIds = [employeeId] };

    /// <summary>Keep whoever holds the appointment if they are free at the new time, otherwise let
    /// it go to anyone else who is. Moving a booking should not move the person unnecessarily.</summary>
    public SlotAssignment Preferring(int? employeeId) =>
        employeeId is { } id && OnDutyIds.Contains(id)
            ? this with { CandidateIds = [id, .. CandidateIds.Where(c => c != id)] }
            : this;
}

public interface IAppointmentRepository
{
    Task<PagedResult<Appointment>> ListAsync(int orgId, DateTime? from, DateTime? to, string? status, int page, int pageSize);
    Task<Appointment?> GetAsync(int orgId, int id);
    /// <summary>Atomically checks capacity/conflicts and inserts. Returns the new id, or null if
    /// the slot is taken. Given an <paramref name="assignment"/>, the employee who gets it is
    /// picked in the same transaction and written back to <see cref="Appointment.EmployeeId"/> on
    /// the argument, so the caller can name them to the person on the phone.</summary>
    Task<int?> TryCreateAsync(Appointment appt, SlotAssignment? assignment = null);
    /// <summary>Atomically checks capacity/conflicts and moves the appointment. Returns false if the new slot is taken.</summary>
    Task<bool> TryRescheduleAsync(int orgId, int id, DateTime startAt, DateTime endAt, SlotAssignment? assignment = null);
    Task UpdateStatusAsync(int orgId, int id, string status);
    Task UpdatePaymentStatusAsync(int orgId, int id, string paymentStatus);
    Task<SidebarCounts> GetSidebarCountsAsync(int orgId);
}

/// <summary>Live badge counts shown next to the sidebar nav items.</summary>
public class SidebarCounts
{
    public int Appointments { get; set; }
    public int Calls { get; set; }
    public int Customers { get; set; }
}

public class AppointmentRepository : IAppointmentRepository
{
    private readonly IDbConnectionFactory _db;
    private readonly ICache _cache;

    public AppointmentRepository(IDbConnectionFactory db, ICache cache)
    {
        _db = db;
        _cache = cache;
    }

    private const string SelectSql = @"
        SELECT a.*, c.Name AS CustomerName, c.Phone AS CustomerPhone, s.Name AS ServiceName,
               u.FullName AS StaffName, e.Name AS EmployeeName
        FROM Appointments a
        JOIN Customers c ON c.Id = a.CustomerId
        LEFT JOIN Services s ON s.Id = a.ServiceId
        LEFT JOIN Users u ON u.Id = a.StaffUserId
        LEFT JOIN Employees e ON e.Id = a.EmployeeId AND e.OrganizationId = a.OrganizationId";

    public async Task<PagedResult<Appointment>> ListAsync(int orgId, DateTime? from, DateTime? to, string? status, int page, int pageSize)
    {
        using var conn = _db.Create();
        var where = "a.OrganizationId = @orgId AND a.IsDeleted = 0";
        if (from.HasValue) where += " AND a.StartAt >= @from";
        if (to.HasValue) where += " AND a.StartAt < @to";
        if (!string.IsNullOrWhiteSpace(status)) where += " AND a.Status = @status";
        var p = new { orgId, from, to, status, skip = (page - 1) * pageSize, take = pageSize };

        var total = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM Appointments a WHERE {where}", p);
        var items = await conn.QueryAsync<Appointment>(
            $"{SelectSql} WHERE {where} ORDER BY a.StartAt DESC, a.Id DESC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY", p);

        return new PagedResult<Appointment> { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
    }

    public async Task<Appointment?> GetAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<Appointment>(
            $"{SelectSql} WHERE a.OrganizationId = @orgId AND a.Id = @id AND a.IsDeleted = 0", new { orgId, id });
    }

    // Range locks (UPDLOCK/HOLDLOCK) inside a transaction make check + insert atomic, so two
    // concurrent AI calls cannot double-book the last free slot.
    //
    // This is the pre-roster rule, and still the one used by every organization with no employees
    // on file: capacity is a single number for the whole business
    // (Organizations.MaxConcurrentAppointments — how many chairs, rooms or vans there are).
    private const string SlotIsFreeSql = @"
        (SELECT COUNT(*) FROM Appointments WITH (UPDLOCK, HOLDLOCK)
         WHERE OrganizationId = @OrganizationId AND IsDeleted = 0
           AND Status NOT IN ('Cancelled','Missed')
           AND StartAt < @EndAt AND EndAt > @StartAt
           AND (@ExcludeId IS NULL OR Id <> @ExcludeId))
        < (SELECT ISNULL(MaxConcurrentAppointments, 1) FROM Organizations WHERE Id = @OrganizationId)
        AND (@StaffUserId IS NULL OR NOT EXISTS
            (SELECT 1 FROM Appointments WITH (UPDLOCK, HOLDLOCK)
             WHERE OrganizationId = @OrganizationId AND IsDeleted = 0
               AND Status NOT IN ('Cancelled','Missed') AND StaffUserId = @StaffUserId
               AND StartAt < @EndAt AND EndAt > @StartAt
               AND (@ExcludeId IS NULL OR Id <> @ExcludeId)))";

    /// <summary>
    /// The roster rule, used once the organization has employees. Sets @Chosen to the person the
    /// booking goes to, or leaves it null when the slot cannot be taken at all.
    ///
    /// Two questions are settled here, and both have to be under the same locks as the write:
    ///
    /// 1. Is anyone free? @FreeOnDuty counts the people working the slot with nothing booked
    ///    across it, and it has to beat @Unassigned — appointments over the slot with nobody
    ///    named, which each still take up one person. Without that, adding a roster to a tenant
    ///    who already had bookings would read two employees as three parallel appointments.
    /// 2. Which of them gets it? The first free candidate in the caller's order of preference.
    ///    @CandidateOrder carries that order as ',id,id,' so the choice breaks the way the caller
    ///    meant rather than by whatever the index happens to return first.
    /// </summary>
    private const string ChooseEmployeeSql = @"
        DECLARE @Chosen INT, @FreeOnDuty INT, @Unassigned INT;

        SELECT @Unassigned = COUNT(*) FROM Appointments WITH (UPDLOCK, HOLDLOCK)
        WHERE OrganizationId = @OrganizationId AND IsDeleted = 0
          AND Status NOT IN ('Cancelled','Missed') AND EmployeeId IS NULL
          AND StartAt < @EndAt AND EndAt > @StartAt
          AND (@ExcludeId IS NULL OR Id <> @ExcludeId);

        SELECT @FreeOnDuty = COUNT(*) FROM Employees e
        WHERE e.OrganizationId = @OrganizationId AND e.IsDeleted = 0 AND e.IsActive = 1
          AND e.Id IN @OnDutyIds
          AND NOT EXISTS (SELECT 1 FROM Appointments a WITH (UPDLOCK, HOLDLOCK)
                          WHERE a.OrganizationId = @OrganizationId AND a.IsDeleted = 0
                            AND a.Status NOT IN ('Cancelled','Missed') AND a.EmployeeId = e.Id
                            AND a.StartAt < @EndAt AND a.EndAt > @StartAt
                            AND (@ExcludeId IS NULL OR a.Id <> @ExcludeId));

        IF @FreeOnDuty > @Unassigned
        SELECT TOP 1 @Chosen = e.Id FROM Employees e
        WHERE e.OrganizationId = @OrganizationId AND e.IsDeleted = 0 AND e.IsActive = 1
          AND e.Id IN @CandidateIds
          AND NOT EXISTS (SELECT 1 FROM Appointments a WITH (UPDLOCK, HOLDLOCK)
                          WHERE a.OrganizationId = @OrganizationId AND a.IsDeleted = 0
                            AND a.Status NOT IN ('Cancelled','Missed') AND a.EmployeeId = e.Id
                            AND a.StartAt < @EndAt AND a.EndAt > @StartAt
                            AND (@ExcludeId IS NULL OR a.Id <> @ExcludeId))
        ORDER BY CHARINDEX(',' + CAST(e.Id AS NVARCHAR(12)) + ',', @CandidateOrder), e.Id;";

    /// <summary>The preference order as ',3,7,'. CHARINDEX against it sorts the candidates the way
    /// the caller listed them; the commas on both ends stop id 7 matching inside id 17.</summary>
    private static string OrderKey(IEnumerable<int> ids) => "," + string.Join(",", ids) + ",";

    public async Task<int?> TryCreateAsync(Appointment a, SlotAssignment? assignment = null)
    {
        using var conn = _db.Create();
        conn.Open();
        using var tx = conn.BeginTransaction();

        int? id;
        if (assignment is null || assignment.OnDutyIds.Count == 0 || assignment.CandidateIds.Count == 0)
        {
            id = await conn.ExecuteScalarAsync<int?>($@"
                INSERT INTO Appointments (OrganizationId, CustomerId, ServiceId, StaffUserId, EmployeeId, StartAt, EndAt, Status, PaymentStatus, Amount, ServiceAddress, AreaStatus, ServiceLocationJson, IsEmergency, Notes)
                OUTPUT INSERTED.Id
                SELECT @OrganizationId, @CustomerId, @ServiceId, @StaffUserId, @EmployeeId, @StartAt, @EndAt, @Status, @PaymentStatus, @Amount, @ServiceAddress, @AreaStatus, @ServiceLocationJson, @IsEmergency, @Notes
                WHERE {SlotIsFreeSql}",
                new
                {
                    a.OrganizationId, a.CustomerId, a.ServiceId, a.StaffUserId, a.EmployeeId, a.StartAt, a.EndAt,
                    a.Status, a.PaymentStatus, a.Amount, a.ServiceAddress, a.AreaStatus, a.ServiceLocationJson, a.IsEmergency, a.Notes,
                    ExcludeId = (int?)null,
                }, tx);
        }
        else
        {
            var row = await conn.QuerySingleOrDefaultAsync<InsertedAppointment>($@"
                {ChooseEmployeeSql}

                INSERT INTO Appointments (OrganizationId, CustomerId, ServiceId, StaffUserId, EmployeeId, StartAt, EndAt, Status, PaymentStatus, Amount, ServiceAddress, AreaStatus, ServiceLocationJson, IsEmergency, Notes)
                OUTPUT INSERTED.Id, INSERTED.EmployeeId
                SELECT @OrganizationId, @CustomerId, @ServiceId, @StaffUserId, @Chosen, @StartAt, @EndAt, @Status, @PaymentStatus, @Amount, @ServiceAddress, @AreaStatus, @ServiceLocationJson, @IsEmergency, @Notes
                WHERE @Chosen IS NOT NULL;",
                new
                {
                    a.OrganizationId, a.CustomerId, a.ServiceId, a.StaffUserId, a.StartAt, a.EndAt,
                    a.Status, a.PaymentStatus, a.Amount, a.ServiceAddress, a.AreaStatus, a.ServiceLocationJson, a.IsEmergency, a.Notes,
                    ExcludeId = (int?)null,
                    OnDutyIds = assignment.OnDutyIds,
                    CandidateIds = assignment.CandidateIds,
                    CandidateOrder = OrderKey(assignment.CandidateIds),
                }, tx);

            id = row?.Id;
            // Written back so the caller can tell the person on the phone who they will be seeing.
            if (row is not null) a.EmployeeId = row.EmployeeId;
        }

        tx.Commit();
        return id;
    }

    private sealed class InsertedAppointment
    {
        public int Id { get; set; }
        public int? EmployeeId { get; set; }
    }

    public async Task<bool> TryRescheduleAsync(int orgId, int id, DateTime startAt, DateTime endAt,
        SlotAssignment? assignment = null)
    {
        using var conn = _db.Create();
        conn.Open();
        using var tx = conn.BeginTransaction();

        int rows;
        if (assignment is null || assignment.OnDutyIds.Count == 0 || assignment.CandidateIds.Count == 0)
        {
            rows = await conn.ExecuteAsync($@"
                UPDATE Appointments SET StartAt = @StartAt, EndAt = @EndAt, Status = 'Scheduled', ModifiedAt = GETUTCDATE()
                WHERE OrganizationId = @OrganizationId AND Id = @ExcludeId AND IsDeleted = 0
                  AND {SlotIsFreeSql}",
                new
                {
                    OrganizationId = orgId, ExcludeId = (int?)id, StartAt = startAt, EndAt = endAt,
                    StaffUserId = (int?)null,
                }, tx);
        }
        else
        {
            // The appointment being moved is excluded from every conflict check (@ExcludeId), so
            // it never blocks itself and can always be nudged within the time it already holds.
            rows = await conn.ExecuteAsync($@"
                {ChooseEmployeeSql}

                UPDATE Appointments SET StartAt = @StartAt, EndAt = @EndAt, EmployeeId = @Chosen,
                    Status = 'Scheduled', ModifiedAt = GETUTCDATE()
                WHERE OrganizationId = @OrganizationId AND Id = @ExcludeId AND IsDeleted = 0
                  AND @Chosen IS NOT NULL;",
                new
                {
                    OrganizationId = orgId, ExcludeId = (int?)id, StartAt = startAt, EndAt = endAt,
                    OnDutyIds = assignment.OnDutyIds,
                    CandidateIds = assignment.CandidateIds,
                    CandidateOrder = OrderKey(assignment.CandidateIds),
                }, tx);
        }

        tx.Commit();
        return rows > 0;
    }

    public async Task UpdatePaymentStatusAsync(int orgId, int id, string paymentStatus)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE Appointments SET PaymentStatus=@paymentStatus, ModifiedAt=GETUTCDATE()
            WHERE OrganizationId=@orgId AND Id=@id AND IsDeleted=0",
            new { orgId, id, paymentStatus });
    }

    /// <summary>Badge counts for the sidebar: appointments awaiting confirmation,
    /// missed calls needing follow-up, and total active customers.</summary>
    /// <summary>
    /// Cached briefly. Three COUNT(*)s is not much on its own, but every open tab asks for them
    /// every minute whether anything happened or not — this collapses a whole team's tabs into
    /// one query per window, and a badge twenty seconds behind is still a badge.
    /// </summary>
    public async Task<SidebarCounts> GetSidebarCountsAsync(int orgId) =>
        await _cache.GetOrSetAsync(CacheKeys.SidebarCounts(orgId), CacheTtl.Counters,
            () => LoadSidebarCountsAsync(orgId)) ?? await LoadSidebarCountsAsync(orgId);

    private async Task<SidebarCounts> LoadSidebarCountsAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleAsync<SidebarCounts>(@"
            SELECT
              (SELECT COUNT(*) FROM Appointments
               WHERE OrganizationId=@orgId AND IsDeleted=0
                 AND Status = 'Scheduled' AND StartAt >= GETUTCDATE()) AS Appointments,
              (SELECT COUNT(*) FROM CallLogs
               WHERE OrganizationId=@orgId AND IsDeleted=0 AND Status='Missed') AS Calls,
              (SELECT COUNT(*) FROM Customers
               WHERE OrganizationId=@orgId AND IsDeleted=0) AS Customers",
            new { orgId });
    }

    public async Task UpdateStatusAsync(int orgId, int id, string status)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE Appointments SET Status=@status, ModifiedAt=GETUTCDATE()
            WHERE OrganizationId=@orgId AND Id=@id AND IsDeleted=0;

            -- Completed visits feed returning-customer history (SRS §7)
            IF @status = 'Completed'
            UPDATE c SET
                LastVisit = a.StartAt,
                FirstVisit = ISNULL(c.FirstVisit, a.StartAt),
                TotalVisits = c.TotalVisits + 1
            FROM Customers c
            JOIN Appointments a ON a.CustomerId = c.Id
            WHERE a.OrganizationId=@orgId AND a.Id=@id;",
            new { orgId, id, status });
    }
}
