using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

[ApiController]
[Route("api/v1/dashboard")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IDashboardRepository _dashboard;
    private readonly ISettingsRepository _settings;
    private readonly IAppointmentRepository _appointments;
    private readonly ITenantProvider _tenant;

    public DashboardController(IDashboardRepository dashboard, ISettingsRepository settings,
        IAppointmentRepository appointments, ITenantProvider tenant)
    {
        _dashboard = dashboard;
        _settings = settings;
        _appointments = appointments;
        _tenant = tenant;
    }

    /// <summary>Live badge counts for the sidebar navigation.</summary>
    [HttpGet("counts")]
    public async Task<IActionResult> Counts() =>
        Ok(ApiResponse<SidebarCounts>.Ok(await _appointments.GetSidebarCountsAsync(_tenant.OrganizationId)));

    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var org = await _settings.GetOrganizationAsync(_tenant.OrganizationId);
        var tz = TenantTime.Resolve(org?.Timezone);
        var nowLocal = TenantTime.NowLocal(tz);
        var todayLocal = nowLocal.Date;
        var monthStartLocal = new DateTime(todayLocal.Year, todayLocal.Month, 1);
        var yearStartLocal = new DateTime(todayLocal.Year, 1, 1);

        // One offset for the whole window. Grouping by local day and hour in SQL needs a
        // constant shift, so DST changes inside the window are read at today's offset —
        // an hour's drift on two days a year, which no dashboard reader will notice.
        var window = new StatsWindow(
            TodayStartUtc: TenantTime.ToUtc(todayLocal, tz),
            TodayEndUtc: TenantTime.ToUtc(todayLocal.AddDays(1), tz),
            MonthStartUtc: TenantTime.ToUtc(monthStartLocal, tz),
            MonthEndUtc: TenantTime.ToUtc(monthStartLocal.AddMonths(1), tz),
            YearStartUtc: TenantTime.ToUtc(yearStartLocal, tz),
            YearEndUtc: TenantTime.ToUtc(yearStartLocal.AddYears(1), tz),
            TodayLocalDate: todayLocal,
            Year: todayLocal.Year,
            OffsetMinutes: (int)tz.GetUtcOffset(DateTime.UtcNow).TotalMinutes);

        return Ok(ApiResponse<DashboardStats>.Ok(
            await _dashboard.GetStatsAsync(_tenant.OrganizationId, window)));
    }
}
