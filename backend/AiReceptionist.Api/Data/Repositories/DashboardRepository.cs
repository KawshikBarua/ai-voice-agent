using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

public class DashboardStats
{
    public int TodaysAppointments { get; set; }
    public int UpcomingAppointments { get; set; }
    public int CancelledAppointments { get; set; }
    public int MissedCalls { get; set; }
    public int TotalCalls { get; set; }
    /// <summary>Calls the AI actually engaged on — answered and either handled end-to-end
    /// or transferred to a human. Excludes missed (no-answer/busy/failed) calls.</summary>
    public int AiHandledCalls { get; set; }
    public int ReturningCustomers { get; set; }
    public decimal Revenue { get; set; }
    public decimal OutstandingPayments { get; set; }
    public double AiBookingRate { get; set; }

    // ---- call volume ----
    public int CallsToday { get; set; }
    /// <summary>Same tenant-local window, one day earlier — the "vs yesterday" delta.</summary>
    public int CallsYesterday { get; set; }
    public int MissedCallsToday { get; set; }
    /// <summary>Mean talk time across every logged call, in seconds.</summary>
    public int AvgCallSeconds { get; set; }

    // ---- talk time / plan ----
    /// <summary>All the AI has ever talked for, in minutes.</summary>
    public int MinutesUsedTotal { get; set; }
    /// <summary>Minutes inside the current billing period (or the current calendar month
    /// when no subscription is on file).</summary>
    public int MinutesUsedThisPeriod { get; set; }
    public int MinutesUsedToday { get; set; }
    /// <summary>Minutes the plan includes per period. 0 = the plan is not metered on minutes,
    /// so the dashboard shows usage without a balance.</summary>
    public int MinutesIncluded { get; set; }
    /// <summary>Never negative — an overrun is reported through <see cref="MinutesOver"/>.</summary>
    public int MinutesRemaining => MinutesIncluded == 0 ? 0 : Math.Max(0, MinutesIncluded - MinutesUsedThisPeriod);
    public int MinutesOver => MinutesIncluded == 0 ? 0 : Math.Max(0, MinutesUsedThisPeriod - MinutesIncluded);
    public string? PlanName { get; set; }
    public string? BillingCycle { get; set; }
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }

    // ---- revenue ----
    public decimal RevenueThisMonth { get; set; }
    public decimal RevenueLastMonth { get; set; }
    public string Currency { get; set; } = "USD";

    // ---- series ----
    public IEnumerable<MonthlyPoint> CallsPerMonth { get; set; } = Enumerable.Empty<MonthlyPoint>();
    public IEnumerable<MonthlyPoint> BookingsPerMonth { get; set; } = Enumerable.Empty<MonthlyPoint>();
    public IEnumerable<MonthlyAmount> RevenuePerMonth { get; set; } = Enumerable.Empty<MonthlyAmount>();
    public IEnumerable<CallOutcomePoint> CallOutcomesPerMonth { get; set; } = Enumerable.Empty<CallOutcomePoint>();
    /// <summary>The last 30 tenant-local days, one row per day that had calls.</summary>
    public IEnumerable<DailyCallPoint> CallsPerDay { get; set; } = Enumerable.Empty<DailyCallPoint>();
    /// <summary>Call count by tenant-local hour of day over the last 30 days — when to staff up.</summary>
    public IEnumerable<HourlyPoint> CallsByHour { get; set; } = Enumerable.Empty<HourlyPoint>();
    public IEnumerable<UpcomingHoliday> UpcomingHolidays { get; set; } = Enumerable.Empty<UpcomingHoliday>();
}

public class MonthlyPoint
{
    public int Month { get; set; }
    public int Value { get; set; }
}

public class MonthlyAmount
{
    public int Month { get; set; }
    public decimal Value { get; set; }
}

public class CallOutcomePoint
{
    public int Month { get; set; }
    public int Handled { get; set; }
    public int Transferred { get; set; }
    public int Missed { get; set; }
}

public class DailyCallPoint
{
    /// <summary>Tenant-local calendar date.</summary>
    public DateTime Date { get; set; }
    public int Handled { get; set; }
    public int Transferred { get; set; }
    public int Missed { get; set; }
    public int Minutes { get; set; }
}

public class HourlyPoint
{
    public int Hour { get; set; }
    public int Value { get; set; }
}

public class UpcomingHoliday
{
    public DateTime Date { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Whole days from the tenant's today. 0 = today, 1 = tomorrow.</summary>
    public int DaysAway { get; set; }
    /// <summary>Appointments already booked on a day the business is closed — the reason
    /// this card is worth looking at.</summary>
    public int AppointmentsBooked { get; set; }
}

/// <summary>Everything the stats query needs to talk in the tenant's local calendar.
/// The offset is the tenant's UTC offset right now, applied in SQL so grouping by
/// day and hour lands on local boundaries.</summary>
public record StatsWindow(
    DateTime TodayStartUtc,
    DateTime TodayEndUtc,
    DateTime MonthStartUtc,
    DateTime MonthEndUtc,
    DateTime YearStartUtc,
    DateTime YearEndUtc,
    DateTime TodayLocalDate,
    int Year,
    int OffsetMinutes);

public interface IDashboardRepository
{
    Task<DashboardStats> GetStatsAsync(int orgId, StatsWindow window);
}

public class DashboardRepository : IDashboardRepository
{
    private readonly IDbConnectionFactory _db;
    public DashboardRepository(IDbConnectionFactory db) => _db = db;

    public async Task<DashboardStats> GetStatsAsync(int orgId, StatsWindow w)
    {
        using var conn = _db.Create();

        var yesterdayStartUtc = w.TodayStartUtc.AddDays(-1);
        var lastMonthStartUtc = w.MonthStartUtc.AddMonths(-1);
        // 30 tenant-local days ending with today.
        var since30Utc = w.TodayEndUtc.AddDays(-30);

        var stats = await conn.QuerySingleAsync<DashboardStats>(@"
            SELECT
              (SELECT COUNT(*) FROM Appointments WHERE OrganizationId=@orgId AND IsDeleted=0
                 AND StartAt >= @todayStartUtc AND StartAt < @todayEndUtc AND Status NOT IN ('Cancelled')) AS TodaysAppointments,
              (SELECT COUNT(*) FROM Appointments WHERE OrganizationId=@orgId AND IsDeleted=0
                 AND StartAt > GETUTCDATE() AND Status IN ('Scheduled','Confirmed')) AS UpcomingAppointments,
              (SELECT COUNT(*) FROM Appointments WHERE OrganizationId=@orgId AND IsDeleted=0
                 AND Status = 'Cancelled') AS CancelledAppointments,
              (SELECT COUNT(*) FROM CallLogs WHERE OrganizationId=@orgId AND IsDeleted=0 AND Status='Missed') AS MissedCalls,
              (SELECT COUNT(*) FROM CallLogs WHERE OrganizationId=@orgId AND IsDeleted=0) AS TotalCalls,
              (SELECT COUNT(*) FROM CallLogs WHERE OrganizationId=@orgId AND IsDeleted=0
                 AND Status IN ('Completed','Transferred')) AS AiHandledCalls,
              (SELECT COUNT(*) FROM Customers WHERE OrganizationId=@orgId AND IsDeleted=0 AND TotalVisits > 1) AS ReturningCustomers,
              (SELECT ISNULL(SUM(Amount),0) FROM Appointments WHERE OrganizationId=@orgId AND IsDeleted=0 AND PaymentStatus='Paid') AS Revenue,
              (SELECT ISNULL(SUM(Amount),0) FROM Appointments WHERE OrganizationId=@orgId AND IsDeleted=0
                 AND PaymentStatus='Unpaid' AND Status NOT IN ('Cancelled')) AS OutstandingPayments,

              (SELECT COUNT(*) FROM CallLogs WHERE OrganizationId=@orgId AND IsDeleted=0
                 AND StartedAt >= @todayStartUtc AND StartedAt < @todayEndUtc) AS CallsToday,
              (SELECT COUNT(*) FROM CallLogs WHERE OrganizationId=@orgId AND IsDeleted=0
                 AND StartedAt >= @yesterdayStartUtc AND StartedAt < @todayStartUtc) AS CallsYesterday,
              (SELECT COUNT(*) FROM CallLogs WHERE OrganizationId=@orgId AND IsDeleted=0 AND Status='Missed'
                 AND StartedAt >= @todayStartUtc AND StartedAt < @todayEndUtc) AS MissedCallsToday,
              (SELECT ISNULL(AVG(CAST(DurationSeconds AS FLOAT)),0) FROM CallLogs
                 WHERE OrganizationId=@orgId AND IsDeleted=0 AND DurationSeconds > 0) AS AvgCallSeconds,

              -- Talk time is billed per started minute, so a 61-second call costs two.
              (SELECT ISNULL(SUM(CEILING(DurationSeconds / 60.0)),0) FROM CallLogs
                 WHERE OrganizationId=@orgId AND IsDeleted=0) AS MinutesUsedTotal,
              (SELECT ISNULL(SUM(CEILING(DurationSeconds / 60.0)),0) FROM CallLogs
                 WHERE OrganizationId=@orgId AND IsDeleted=0
                   AND StartedAt >= @todayStartUtc AND StartedAt < @todayEndUtc) AS MinutesUsedToday,

              (SELECT ISNULL(SUM(Amount),0) FROM Appointments WHERE OrganizationId=@orgId AND IsDeleted=0
                 AND PaymentStatus='Paid' AND StartAt >= @monthStartUtc AND StartAt < @monthEndUtc) AS RevenueThisMonth,
              (SELECT ISNULL(SUM(Amount),0) FROM Appointments WHERE OrganizationId=@orgId AND IsDeleted=0
                 AND PaymentStatus='Paid' AND StartAt >= @lastMonthStartUtc AND StartAt < @monthStartUtc) AS RevenueLastMonth,
              (SELECT Currency FROM Organizations WHERE Id=@orgId) AS Currency",
            new
            {
                orgId,
                todayStartUtc = w.TodayStartUtc,
                todayEndUtc = w.TodayEndUtc,
                yesterdayStartUtc,
                monthStartUtc = w.MonthStartUtc,
                monthEndUtc = w.MonthEndUtc,
                lastMonthStartUtc,
            });

        stats.CallsPerMonth = await conn.QueryAsync<MonthlyPoint>(@"
            SELECT MONTH(DATEADD(minute, @offset, StartedAt)) AS Month, COUNT(*) AS Value FROM CallLogs
            WHERE OrganizationId=@orgId AND IsDeleted=0 AND StartedAt >= @yearStartUtc AND StartedAt < @yearEndUtc
            GROUP BY MONTH(DATEADD(minute, @offset, StartedAt)) ORDER BY Month",
            new { orgId, offset = w.OffsetMinutes, yearStartUtc = w.YearStartUtc, yearEndUtc = w.YearEndUtc });

        stats.BookingsPerMonth = await conn.QueryAsync<MonthlyPoint>(@"
            SELECT MONTH(DATEADD(minute, @offset, CreatedAt)) AS Month, COUNT(*) AS Value FROM Appointments
            WHERE OrganizationId=@orgId AND IsDeleted=0 AND CreatedAt >= @yearStartUtc AND CreatedAt < @yearEndUtc
            GROUP BY MONTH(DATEADD(minute, @offset, CreatedAt)) ORDER BY Month",
            new { orgId, offset = w.OffsetMinutes, yearStartUtc = w.YearStartUtc, yearEndUtc = w.YearEndUtc });

        // Revenue is booked to the month the appointment happens in, matching the
        // "revenue this month" tile above.
        stats.RevenuePerMonth = await conn.QueryAsync<MonthlyAmount>(@"
            SELECT MONTH(DATEADD(minute, @offset, StartAt)) AS Month, ISNULL(SUM(Amount),0) AS Value
            FROM Appointments
            WHERE OrganizationId=@orgId AND IsDeleted=0 AND PaymentStatus='Paid'
              AND StartAt >= @yearStartUtc AND StartAt < @yearEndUtc
            GROUP BY MONTH(DATEADD(minute, @offset, StartAt)) ORDER BY Month",
            new { orgId, offset = w.OffsetMinutes, yearStartUtc = w.YearStartUtc, yearEndUtc = w.YearEndUtc });

        stats.CallOutcomesPerMonth = await conn.QueryAsync<CallOutcomePoint>(@"
            SELECT MONTH(DATEADD(minute, @offset, StartedAt)) AS Month,
                   SUM(CASE WHEN Status='Completed'   THEN 1 ELSE 0 END) AS Handled,
                   SUM(CASE WHEN Status='Transferred' THEN 1 ELSE 0 END) AS Transferred,
                   SUM(CASE WHEN Status='Missed'      THEN 1 ELSE 0 END) AS Missed
            FROM CallLogs
            WHERE OrganizationId=@orgId AND IsDeleted=0 AND StartedAt >= @yearStartUtc AND StartedAt < @yearEndUtc
            GROUP BY MONTH(DATEADD(minute, @offset, StartedAt)) ORDER BY Month",
            new { orgId, offset = w.OffsetMinutes, yearStartUtc = w.YearStartUtc, yearEndUtc = w.YearEndUtc });

        stats.CallsPerDay = await conn.QueryAsync<DailyCallPoint>(@"
            SELECT CAST(DATEADD(minute, @offset, StartedAt) AS DATE) AS [Date],
                   SUM(CASE WHEN Status='Completed'   THEN 1 ELSE 0 END) AS Handled,
                   SUM(CASE WHEN Status='Transferred' THEN 1 ELSE 0 END) AS Transferred,
                   SUM(CASE WHEN Status='Missed'      THEN 1 ELSE 0 END) AS Missed,
                   ISNULL(SUM(CEILING(DurationSeconds / 60.0)),0) AS Minutes
            FROM CallLogs
            WHERE OrganizationId=@orgId AND IsDeleted=0 AND StartedAt >= @since30Utc AND StartedAt < @todayEndUtc
            GROUP BY CAST(DATEADD(minute, @offset, StartedAt) AS DATE)
            ORDER BY [Date]",
            new { orgId, offset = w.OffsetMinutes, since30Utc, todayEndUtc = w.TodayEndUtc });

        stats.CallsByHour = await conn.QueryAsync<HourlyPoint>(@"
            SELECT DATEPART(hour, DATEADD(minute, @offset, StartedAt)) AS Hour, COUNT(*) AS Value
            FROM CallLogs
            WHERE OrganizationId=@orgId AND IsDeleted=0 AND StartedAt >= @since30Utc AND StartedAt < @todayEndUtc
            GROUP BY DATEPART(hour, DATEADD(minute, @offset, StartedAt)) ORDER BY Hour",
            new { orgId, offset = w.OffsetMinutes, since30Utc, todayEndUtc = w.TodayEndUtc });

        // Closures the business has ahead of it, with the appointments already sitting on
        // those days — the number that turns a reminder into an action.
        stats.UpcomingHolidays = (await conn.QueryAsync<UpcomingHoliday>(@"
            SELECT TOP (5) h.[Date], h.Name,
                   DATEDIFF(day, @todayLocal, h.[Date]) AS DaysAway,
                   (SELECT COUNT(*) FROM Appointments a
                     WHERE a.OrganizationId = h.OrganizationId AND a.IsDeleted = 0
                       AND a.Status NOT IN ('Cancelled')
                       AND CAST(DATEADD(minute, @offset, a.StartAt) AS DATE) = h.[Date]) AS AppointmentsBooked
            FROM Holidays h
            WHERE h.OrganizationId=@orgId AND h.IsDeleted=0 AND h.[Date] >= @todayLocal
            ORDER BY h.[Date]",
            new { orgId, offset = w.OffsetMinutes, todayLocal = w.TodayLocalDate })).ToList();

        await LoadSubscriptionAsync(conn, orgId, stats, w);

        // Booking conversion for the same year the charts cover: of the calls the AI
        // handled this year, how many turned into a booking. Both sides are year-scoped
        // so the rate stays meaningful and is clamped to 0–100%.
        var handledThisYear = stats.CallOutcomesPerMonth.Sum(c => c.Handled + c.Transferred);
        var bookings = stats.BookingsPerMonth.Sum(b => b.Value);
        stats.AiBookingRate = handledThisYear == 0
            ? 0
            : Math.Round(Math.Min(bookings / (double)handledThisYear * 100, 100), 1);
        return stats;
    }

    /// <summary>Plan and minutes balance. The subscription table belongs to the super admin
    /// app: it may not exist yet in a tenant-only deployment, so a missing table simply means
    /// "no plan on file" and the period falls back to the current calendar month.</summary>
    private static async Task LoadSubscriptionAsync(
        System.Data.IDbConnection conn, int orgId, DashboardStats stats, StatsWindow w)
    {
        // Two steps rather than one guarded batch: the column reference alone would fail to
        // compile if the super admin app has never run against this database.
        var planTableReady = await conn.ExecuteScalarAsync<int>(@"
            SELECT CASE WHEN COL_LENGTH('OrganizationSubscriptions','IncludedMinutes') IS NULL
                        THEN 0 ELSE 1 END");

        var plan = planTableReady == 0 ? null : await conn.QuerySingleOrDefaultAsync<PlanRow>(@"
            SELECT PlanName, BillingCycle, IncludedMinutes, CurrentPeriodStart, CurrentPeriodEnd,
                   CASE WHEN PlanId IS NULL AND Amount = 0 AND IncludedMinutes = 0 THEN 0 ELSE 1 END AS HasPlan
            FROM OrganizationSubscriptions WHERE OrganizationId = @orgId",
            new { orgId });

        if (plan is { HasPlan: false }) plan = null;

        // The *usage* window, not the subscription's CurrentPeriodStart/End.
        //
        // Those two track access — they move only when a payment is recorded, and stop when one is
        // not. Counting minutes against them made this tile disagree with the billing page, which
        // has always counted into fresh windows anchored on the last period closed. Two different
        // answers to "how many minutes have I used" is the one thing a usage figure cannot afford,
        // so both now read the same window (see UsageWindows).
        DateTime periodStart, periodEnd;
        if (plan is null)
        {
            periodStart = w.MonthStartUtc;
            periodEnd = w.MonthEndUtc;
        }
        else
        {
            var anchor = await conn.ExecuteScalarAsync<DateTime?>(@"
                SELECT MAX(PeriodEnd) FROM OrganizationUsagePeriods WHERE OrganizationId = @orgId",
                new { orgId }) ?? plan.CurrentPeriodStart;

            (periodStart, periodEnd) = UsageWindows.Current(anchor, plan.BillingCycle, DateTime.UtcNow);
        }

        stats.PlanName = plan?.PlanName;
        stats.BillingCycle = plan?.BillingCycle;
        stats.MinutesIncluded = plan?.IncludedMinutes ?? 0;
        stats.PeriodStart = periodStart;
        stats.PeriodEnd = periodEnd;

        // BillingSchema.MinutesExpression, so the rounding is literally the same expression the
        // invoice is computed with rather than a copy that could drift from it.
        stats.MinutesUsedThisPeriod = await conn.ExecuteScalarAsync<int>($@"
            SELECT {BillingSchema.MinutesExpression} FROM CallLogs
            WHERE OrganizationId=@orgId AND IsDeleted=0
              AND StartedAt >= @periodStart AND StartedAt < @periodEnd",
            new { orgId, periodStart, periodEnd });
    }

    private class PlanRow
    {
        public string? PlanName { get; set; }
        public string? BillingCycle { get; set; }
        public int IncludedMinutes { get; set; }
        public DateTime CurrentPeriodStart { get; set; }
        public DateTime CurrentPeriodEnd { get; set; }

        /// <summary>False for the row that exists only to hold a Stripe customer link before a
        /// customer has chosen a tier — see <see cref="SubscriptionRecord.HasPlan"/>. The dashboard
        /// must read that as no plan, or it would announce a 0-minute "No plan" allowance.</summary>
        public bool HasPlan { get; set; }
    }
}
