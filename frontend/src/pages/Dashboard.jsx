import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from 'react-router-dom'
import { api, unwrap } from '../api/client'
import {
  Card, CardTitle, PillButton, Chip, Avatar, EmptyState, Segmented, Delta, statusTone,
} from '../components/ui'
import { StackedColumns, AreaTrend, GaugeMeter, BarMeter, Sparkline, HourHeat, Legend } from '../components/charts'
import {
  MONTHS, compact, money, moneyExact, minutes as fmtMinutes, duration, pctChange, whenLabel,
} from '../lib/format'

/* The three call outcomes, in the order they stack. One validated categorical set —
   see the palette note in index.css. */
const OUTCOMES = [
  { key: 'handled', label: 'Handled by AI', color: 'var(--color-viz-handled)' },
  { key: 'transferred', label: 'Transferred', color: 'var(--color-viz-transferred)' },
  { key: 'missed', label: 'Missed', color: 'var(--color-viz-missed)' },
]

const CALL_RANGES = [
  { value: '7d', label: '7 days' },
  { value: '30d', label: '30 days' },
  { value: '12m', label: '12 months' },
]

const APPT_RANGES = {
  today: { label: 'Today', days: 1 },
  tomorrow: { label: 'Tomorrow', days: 1, offset: 1 },
  week: { label: 'Next 7 days', days: 7 },
}

// ---------------------------------------------------------------------------
// Offline sample data — keeps the dashboard readable (and clearly labelled) when
// the API cannot be reached, rather than showing a screen of zeroes.
// ---------------------------------------------------------------------------
const DEMO_STATS = (() => {
  const month = new Date().getMonth()
  const seeded = (i, a, b) => a + ((i * 37 + 11) % (b - a + 1))
  const days = Array.from({ length: 30 }, (_, i) => {
    const d = new Date()
    d.setDate(d.getDate() - (29 - i))
    const weekend = d.getDay() === 0
    const handled = weekend ? 0 : seeded(i, 5, 14)
    return {
      date: d.toISOString().slice(0, 10),
      handled,
      transferred: weekend ? 0 : seeded(i + 3, 0, 4),
      missed: weekend ? 0 : seeded(i + 7, 0, 2),
      minutes: handled * seeded(i, 3, 6),
    }
  })
  return {
    demo: true,
    currency: 'USD',
    todaysAppointments: 6, upcomingAppointments: 14, cancelledAppointments: 2,
    missedCalls: 23, totalCalls: 486, aiHandledCalls: 463, returningCustomers: 38,
    revenue: 42750, outstandingPayments: 1860, aiBookingRate: 46.2,
    callsToday: 18, callsYesterday: 14, missedCallsToday: 1, avgCallSeconds: 214,
    minutesUsedTotal: 3184, minutesUsedThisPeriod: 742, minutesUsedToday: 61,
    minutesIncluded: 1200, minutesRemaining: 458, minutesOver: 0,
    planName: 'Growth', billingCycle: 'Monthly',
    periodStart: new Date(new Date().getFullYear(), month, 1).toISOString(),
    periodEnd: new Date(new Date().getFullYear(), month + 1, 1).toISOString(),
    revenueThisMonth: 6420, revenueLastMonth: 5810,
    callsPerMonth: [], bookingsPerMonth: [],
    revenuePerMonth: [3100, 3480, 4020, 3860, 4610, 5150, 4880, 5340, 5900, 6100, 5700, 6300]
      .slice(0, month + 1)
      // The last two months are pinned to the tiles above, so the card and the chart agree.
      .map((v, i, all) => ({
        month: i + 1,
        value: i === all.length - 1 ? 6420 : i === all.length - 2 ? 5810 : v,
      })),
    callOutcomesPerMonth: Array.from({ length: month + 1 }, (_, i) => ({
      month: i + 1,
      handled: seeded(i, 120, 190),
      transferred: seeded(i + 2, 14, 34),
      missed: seeded(i + 5, 3, 12),
    })),
    callsPerDay: days,
    callsByHour: Array.from({ length: 24 }, (_, h) => ({
      hour: h,
      value: h < 8 || h > 19 ? seeded(h, 0, 2) : seeded(h, 8, 34),
    })),
    upcomingHolidays: [
      { date: new Date(Date.now() + 9 * 864e5).toISOString().slice(0, 10), name: 'Staff training day', daysAway: 9, appointmentsBooked: 2 },
      { date: new Date(Date.now() + 34 * 864e5).toISOString().slice(0, 10), name: 'Public holiday — closed', daysAway: 34, appointmentsBooked: 0 },
    ],
  }
})()

// ---------------------------------------------------------------------------
// Shaping
// ---------------------------------------------------------------------------

const dayKey = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`

/** The API returns only days that had calls; the chart needs an unbroken run of them. */
function lastDays(points, n) {
  const byDate = new Map((points ?? []).map((p) => [String(p.date).slice(0, 10), p]))
  return Array.from({ length: n }, (_, i) => {
    const d = new Date()
    d.setHours(0, 0, 0, 0)
    d.setDate(d.getDate() - (n - 1 - i))
    const hit = byDate.get(dayKey(d)) ?? {}
    return {
      date: d,
      handled: hit.handled ?? 0,
      transferred: hit.transferred ?? 0,
      missed: hit.missed ?? 0,
      minutes: hit.minutes ?? 0,
    }
  })
}

/** Months elapsed this year — never padded out with empty future months. */
function monthsElapsed(points, field = 'value') {
  const upto = new Date().getMonth()
  const by = new Map((points ?? []).map((p) => [p.month, p]))
  return Array.from({ length: upto + 1 }, (_, i) => ({
    month: i + 1,
    value: Number(by.get(i + 1)?.[field] ?? 0),
    row: by.get(i + 1),
  }))
}

const hourLabel = (h) => (h === 0 ? '12 AM' : h < 12 ? `${h} AM` : h === 12 ? '12 PM' : `${h - 12} PM`)

// ---------------------------------------------------------------------------
// Stat tiles
// ---------------------------------------------------------------------------

function StatTile({ label, value, unit, footnote, delta, featured, to, children }) {
  const body = (
    <div className={`flex h-full flex-col rounded-card p-4 shadow-sm transition sm:p-5 ${
      featured ? 'bg-ink text-on-ink' : 'bg-card hover:shadow-md'
    }`}>
      <div className="flex items-start justify-between gap-2">
        <p className={`text-[12px] font-medium ${featured ? 'opacity-70' : 'text-muted'}`}>{label}</p>
        {to && (
          <span aria-hidden="true" className={`grid h-6 w-6 shrink-0 place-items-center rounded-full text-[11px] ${
            featured ? 'bg-on-ink/15' : 'bg-panel text-ink-soft'
          }`}>↗</span>
        )}
      </div>
      <p className="mt-2 flex flex-wrap items-baseline gap-x-1.5">
        <span className="text-[27px] font-bold leading-none sm:text-[30px]">{value}</span>
        {unit && <span className={`text-[12.5px] font-medium ${featured ? 'opacity-70' : 'text-muted'}`}>{unit}</span>}
      </p>
      <div className="mt-3 flex-1">{children}</div>
      <div className="mt-3 min-h-[18px]">
        {delta !== undefined ? delta : (
          <p className={`text-[11.5px] ${featured ? 'opacity-70' : 'text-muted'}`}>{footnote}</p>
        )}
      </div>
    </div>
  )
  return to ? <Link to={to} className="block h-full">{body}</Link> : body
}

/** The four numbers under the call chart — the quality behind the volume. */
function MiniStat({ label, value, hint }) {
  return (
    <div className="rounded-2xl bg-panel px-3.5 py-3">
      <p className="text-[11px] font-medium text-muted">{label}</p>
      <p className="mt-1 text-[17px] font-bold leading-none">{value}</p>
      {hint && <p className="mt-1 text-[10.5px] text-muted">{hint}</p>}
    </div>
  )
}

// ---------------------------------------------------------------------------

export default function Dashboard() {
  const navigate = useNavigate()
  const [search, setSearch] = useState('')
  // 30 columns in a phone-width card is a picket fence; a week reads properly there and
  // the other ranges are one tap away.
  const [callRange, setCallRange] = useState(
    () => (typeof window !== 'undefined' && window.matchMedia('(max-width: 640px)').matches ? '7d' : '30d'),
  )
  const [callView, setCallView] = useState('chart')
  const [apptRange, setApptRange] = useState('today')

  const { data: stats, isError } = useQuery({
    queryKey: ['dashboard'],
    queryFn: () => api.get('/dashboard/stats').then(unwrap),
  })

  const { from, to } = useMemo(() => {
    const r = APPT_RANGES[apptRange]
    const start = new Date()
    start.setHours(0, 0, 0, 0)
    if (r.offset) start.setDate(start.getDate() + r.offset)
    const end = new Date(start)
    end.setDate(end.getDate() + r.days)
    return { from: start.toISOString(), to: end.toISOString() }
  }, [apptRange])

  const { data: appts } = useQuery({
    queryKey: ['appointments', 'dashboard', apptRange],
    queryFn: () => api.get('/appointments', { params: { from, to, pageSize: 5 } }).then(unwrap),
  })

  const { data: kb } = useQuery({
    queryKey: ['knowledge-base'],
    queryFn: () => api.get('/knowledge-base').then(unwrap),
  })

  const { data: retell } = useQuery({
    queryKey: ['retell-status'],
    queryFn: () => api.get('/retell/status').then(unwrap),
  })

  const s = stats ?? DEMO_STATS
  const isDemo = !stats
  const cur = s.currency ?? 'USD'

  const days30 = useMemo(() => lastDays(s.callsPerDay, 30), [s.callsPerDay])
  const monthlyOutcomes = useMemo(() => monthsElapsed(s.callOutcomesPerMonth, 'handled'), [s.callOutcomesPerMonth])
  const revenueMonths = useMemo(() => monthsElapsed(s.revenuePerMonth), [s.revenuePerMonth])

  const callSeries = useMemo(() => {
    if (callRange === '12m') {
      return monthlyOutcomes.map((m) => ({
        label: MONTHS[m.month - 1],
        sublabel: `${MONTHS[m.month - 1]} ${new Date().getFullYear()}`,
        values: [m.row?.handled ?? 0, m.row?.transferred ?? 0, m.row?.missed ?? 0],
      }))
    }
    return days30.slice(callRange === '7d' ? -7 : -30).map((d) => ({
      label: d.date.toLocaleDateString(undefined, { day: 'numeric' }),
      sublabel: d.date.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' }),
      values: [d.handled, d.transferred, d.missed],
    }))
  }, [callRange, days30, monthlyOutcomes])

  const callTotals = useMemo(
    () => callSeries.reduce((acc, d) => acc.map((v, i) => v + d.values[i]), [0, 0, 0]),
    [callSeries],
  )
  const callGrand = callTotals.reduce((a, b) => a + b, 0)

  // Plan / talk time
  const included = s.minutesIncluded ?? 0
  const usedPeriod = s.minutesUsedThisPeriod ?? 0
  const remaining = Math.max(0, included - usedPeriod)
  const overBy = Math.max(0, usedPeriod - included)
  const periodEnd = s.periodEnd ? new Date(s.periodEnd) : null
  const daysToRenew = periodEnd ? Math.max(0, Math.ceil((periodEnd - Date.now()) / 864e5)) : null

  const revenueTotal = revenueMonths.reduce((a, m) => a + m.value, 0)
  const bestMonth = revenueMonths.reduce((best, m) => (m.value > (best?.value ?? -1) ? m : best), null)
  const answerRate = s.totalCalls > 0 ? ((s.totalCalls - s.missedCalls) / s.totalCalls) * 100 : 0

  const todayAppts = appts?.items ?? []
  const fmtTime = (iso) => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })

  const submitSearch = (e) => {
    e.preventDefault()
    if (search.trim()) navigate(`/customers?q=${encodeURIComponent(search.trim())}`)
  }

  return (
    <div>
      {/* ---------------- header ---------------- */}
      <div className="mb-5 flex flex-wrap items-start justify-between gap-x-4 gap-y-3 sm:mb-6">
        <div className="min-w-0">
          <h1 className="text-[22px] font-bold leading-tight sm:text-[26px]">Dashboard</h1>
          <p className="mt-1 text-[12.5px] text-muted sm:text-[13px]">
            How your AI receptionist performed{isDemo ? ' — sample figures' : ''}.
          </p>
        </div>
        {/* Below sm the actions take the full row and share it evenly, rather than
            crowding into whatever space the title leaves. */}
        <div className="flex w-full flex-wrap items-center gap-2.5 sm:w-auto">
          {isError && <Chip tone="red">Offline · sample data</Chip>}
          <form onSubmit={submitSearch}
            className="hidden items-center gap-2 rounded-pill bg-card px-4 py-2.5 shadow-sm lg:flex">
            <button type="submit" aria-label="Search customers" className="grid place-items-center">
              <svg viewBox="0 0 24 24" className="h-4 w-4 text-muted" fill="none" stroke="currentColor" strokeWidth="2">
                <circle cx="11" cy="11" r="7" /><path d="m21 21-4.3-4.3" />
              </svg>
            </button>
            <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search customers…"
              className="w-32 bg-transparent text-[13px] outline-none placeholder:text-muted xl:w-36" />
          </form>
          <Link to="/calls" className="flex-1 sm:flex-none">
            <PillButton variant="outline" className="w-full sm:w-auto">View calls</PillButton>
          </Link>
          <Link to="/appointments" className="flex-1 sm:flex-none">
            <PillButton className="w-full sm:w-auto">
              <span className="sm:hidden">+ Appointment</span>
              <span className="hidden sm:inline">+ New appointment</span>
            </PillButton>
          </Link>
        </div>
      </div>

      {/* ---------------- KPI row ---------------- */}
      <div className="@2xl:grid-cols-2 @4xl:grid-cols-4 mb-4 grid grid-cols-1 gap-3.5 sm:mb-5 sm:gap-4">
        <StatTile
          featured
          label="Calls today"
          value={s.callsToday ?? 0}
          unit={s.missedCallsToday ? `· ${s.missedCallsToday} missed` : null}
          to="/calls"
          delta={<Delta value={pctChange(s.callsToday, s.callsYesterday)} since="vs yesterday" />}
        >
          <Sparkline values={days30.slice(-14).map((d) => d.handled + d.transferred + d.missed)}
            color="var(--color-on-ink)" />
        </StatTile>

        <StatTile
          label="Minutes called"
          value={compact(s.minutesUsedTotal ?? 0)}
          unit="min all time"
          to="/calls"
          footnote={`${fmtMinutes(usedPeriod)} this billing period`}
        >
          <Sparkline values={days30.slice(-14).map((d) => d.minutes)} color="var(--color-viz-transferred)" />
        </StatTile>

        {/* Without an allowance there is no balance to show, so the tile falls back to the
            period's usage — a number, rather than a dash where a number should be. */}
        <StatTile
          label={included > 0 ? 'Plan minutes' : 'Minutes this period'}
          value={included > 0 ? included.toLocaleString() : compact(usedPeriod)}
          unit={included > 0
            ? `min / ${(s.billingCycle ?? 'month').toLowerCase().replace('ly', '')}`
            : 'min · not metered'}
          to="/settings"
          footnote={
            included === 0 ? 'No minute allowance on this plan'
              : overBy > 0 ? `${fmtMinutes(overBy)} over the allowance`
                : `${fmtMinutes(remaining)} remaining${daysToRenew != null ? ` · renews in ${daysToRenew}d` : ''}`
          }
        >
          {included > 0 && (
            <BarMeter
              ratio={usedPeriod / included}
              tone={overBy > 0 ? 'var(--color-crit)' : usedPeriod / included > 0.8 ? 'var(--color-warn)' : 'var(--color-viz-fill)'}
              className="mt-2"
            />
          )}
        </StatTile>

        <StatTile
          label="Revenue this month"
          value={money(s.revenueThisMonth ?? 0, cur)}
          to="/appointments?status=Completed"
          delta={<Delta value={pctChange(s.revenueThisMonth, s.revenueLastMonth)} since="vs last month" />}
        >
          <p className="text-[11.5px] text-muted">
            {moneyExact(s.outstandingPayments ?? 0, cur)} still outstanding
          </p>
        </StatTile>
      </div>

      {/*
        ---------------- main grid ----------------
        Measured against the content column, not the window, so the sidebar appearing is
        just another width change. The card order is what makes the middle size work: with
        Call · Talk · Closures · Revenue · Appointments · Side, auto-placement leaves no
        holes at either two or three columns.
          narrow  1 col  — everything stacked
          672px+  2 cols — full-width chart, then a pair, then the full-width trend
          1024px+ 3 cols — wide card and narrow card side by side, alternating sides
      */}
      <div className="@2xl:grid-cols-2 @5xl:grid-cols-3 grid grid-cols-1 gap-4 sm:gap-5">
        {/* Call analytics — its own container, so the strip of figures below the chart
            measures against the card rather than the page. */}
        <Card className="@container @2xl:col-span-2 flex flex-col">
          <CardTitle
            title="Call analytics"
            subtitle="Every inbound call, split by how it ended"
            action={
              <div className="flex flex-wrap items-center justify-end gap-2">
                <Segmented value={callView} onChange={setCallView} size="sm"
                  options={[{ value: 'chart', label: 'Chart' }, { value: 'table', label: 'Table' }]} />
                <Segmented value={callRange} onChange={setCallRange} options={CALL_RANGES} />
              </div>
            }
          />

          <Legend
            className="mb-3"
            items={OUTCOMES.map((o, i) => ({ ...o, value: callTotals[i].toLocaleString() }))}
          />

          {callView === 'chart' ? (
            <StackedColumns
              data={callSeries}
              series={OUTCOMES}
              height={248}
              ariaLabel={`Calls by outcome over the last ${CALL_RANGES.find((r) => r.value === callRange).label}. Switch to the table view for the figures.`}
            />
          ) : (
            <div className="max-h-[248px] overflow-auto rounded-2xl bg-panel">
              <table className="w-full min-w-[24rem] text-[12px]">
                <thead className="sticky top-0 bg-panel text-muted">
                  <tr>
                    <th className="px-3 py-2 text-left font-medium">{callRange === '12m' ? 'Month' : 'Day'}</th>
                    {OUTCOMES.map((o) => (
                      <th key={o.key} className="px-3 py-2 text-right font-medium">{o.label}</th>
                    ))}
                    <th className="px-3 py-2 text-right font-medium">Total</th>
                  </tr>
                </thead>
                <tbody>
                  {[...callSeries].reverse().map((d) => (
                    <tr key={d.sublabel} className="border-t border-line/70">
                      <td className="whitespace-nowrap px-3 py-1.5">{d.sublabel}</td>
                      {d.values.map((v, i) => (
                        <td key={i} className="px-3 py-1.5 text-right tabular-nums">{v}</td>
                      ))}
                      <td className="px-3 py-1.5 text-right font-semibold tabular-nums">
                        {d.values.reduce((a, b) => a + b, 0)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          <div className="@xl:grid-cols-4 mt-5 grid grid-cols-2 gap-3">
            <MiniStat label="Answer rate" value={`${answerRate.toFixed(1)}%`} hint="calls the AI picked up" />
            <MiniStat label="Booked by AI" value={`${s.aiBookingRate ?? 0}%`} hint="of handled calls this year" />
            <MiniStat label="Avg call length" value={duration(s.avgCallSeconds ?? 0)} hint="across all calls" />
            <MiniStat label="In this view" value={compact(callGrand)} hint={`calls · ${CALL_RANGES.find((r) => r.value === callRange).label}`} />
          </div>
        </Card>

        {/* Talk-time balance */}
        <Card className="flex flex-col">
          <CardTitle
            title="Talk-time balance"
            subtitle={s.planName ? `${s.planName} plan · ${(s.billingCycle ?? 'Monthly').toLowerCase()}` : 'No plan on file'}
          />
          {included > 0 ? (
            <>
              <GaugeMeter used={usedPeriod} allowance={included}
                caption={`${fmtMinutes(usedPeriod)} of ${included.toLocaleString()} used`} />
              {overBy > 0 && (
                <p className="mt-2 rounded-2xl bg-danger-soft px-3.5 py-2.5 text-[12px] font-medium text-danger">
                  ⚠ {fmtMinutes(overBy)} over the allowance this period.
                </p>
              )}
            </>
          ) : (
            <GaugeMeter used={usedPeriod} allowance={0}
              caption="This plan is not metered on minutes" />
          )}

          <dl className="mt-4 space-y-2.5 text-[12.5px]">
            <div className="flex items-center justify-between gap-3">
              <dt className="text-muted">Used today</dt>
              <dd className="font-semibold tabular-nums">{fmtMinutes(s.minutesUsedToday ?? 0)}</dd>
            </div>
            <div className="flex items-center justify-between gap-3">
              <dt className="text-muted">{s.planName ? 'This period' : 'This month'}</dt>
              <dd className="font-semibold tabular-nums">{fmtMinutes(usedPeriod)}</dd>
            </div>
            <div className="flex items-center justify-between gap-3">
              <dt className="text-muted">All time</dt>
              <dd className="font-semibold tabular-nums">{fmtMinutes(s.minutesUsedTotal ?? 0)}</dd>
            </div>
            {/* Only meaningful with a plan behind it — without one the period is just
                the calendar month the usage above is counted over. */}
            {periodEnd && s.planName && (
              <div className="flex items-center justify-between gap-3">
                <dt className="text-muted">Period renews</dt>
                <dd className="font-semibold">
                  {periodEnd.toLocaleDateString(undefined, { day: 'numeric', month: 'short' })}
                  {daysToRenew != null && <span className="ml-1 font-medium text-muted">({daysToRenew}d)</span>}
                </dd>
              </div>
            )}
          </dl>

          <div className="mt-auto pt-4">
            <Link to="/settings">
              <PillButton variant="outline" className="w-full py-2.5">Manage plan</PillButton>
            </Link>
          </div>
        </Card>

        {/* Upcoming closures — what the calendar used to sit in */}
        <Card className="flex flex-col">
          <CardTitle title="Upcoming closures" subtitle="Days the AI will tell callers you are shut" />
          {(s.upcomingHolidays?.length ?? 0) === 0 ? (
            <EmptyState message="No closures scheduled. Add one in Settings and the AI stops taking bookings for that day." />
          ) : (
            <div className="space-y-3">
              {s.upcomingHolidays.map((h, i) => {
                const date = new Date(`${String(h.date).slice(0, 10)}T00:00:00`)
                const next = i === 0
                return (
                  <div key={`${h.date}-${h.name}`}
                    className={`flex items-start gap-3.5 rounded-2xl p-3.5 ${next ? 'bg-cream' : 'bg-panel'}`}>
                    <div className="grid h-12 w-12 shrink-0 place-items-center rounded-2xl bg-card text-center leading-none shadow-sm">
                      <span className="block text-[9.5px] font-semibold uppercase tracking-wide text-muted">
                        {MONTHS[date.getMonth()]}
                      </span>
                      <span className="block text-[16px] font-bold">{date.getDate()}</span>
                    </div>
                    <div className="min-w-0 flex-1">
                      <p className="text-[13.5px] font-bold leading-snug">{h.name}</p>
                      <p className="mt-0.5 text-[11.5px] text-ink-soft">
                        {date.toLocaleDateString(undefined, { weekday: 'long' })} · {whenLabel(h.daysAway)}
                      </p>
                      {h.appointmentsBooked > 0 && (
                        <p className="mt-0.5 text-[11.5px] font-semibold text-danger">
                          {h.appointmentsBooked} appointment{h.appointmentsBooked === 1 ? '' : 's'} still booked
                        </p>
                      )}
                    </div>
                    {next && <Chip tone="dark">Next</Chip>}
                  </div>
                )
              })}
            </div>
          )}
          <div className="mt-auto pt-4">
            <Link to="/settings">
              <PillButton variant="outline" className="w-full py-2.5">Manage closures</PillButton>
            </Link>
          </div>
        </Card>

        {/* Revenue analytics */}
        <Card className="@2xl:col-span-2">
          <CardTitle
            title="Revenue analytics"
            subtitle={`Paid appointments, month by month · ${new Date().getFullYear()}`}
            action={
              <div className="flex gap-5 sm:gap-6">
                <div className="text-right">
                  <p className="text-[11px] text-muted">Year to date</p>
                  <p className="text-[15px] font-bold leading-tight sm:text-[17px]">{moneyExact(revenueTotal, cur)}</p>
                </div>
                <div className="text-right">
                  <p className="text-[11px] text-muted">Best month</p>
                  <p className="text-[15px] font-bold leading-tight sm:text-[17px]">
                    {bestMonth && bestMonth.value > 0 ? MONTHS[bestMonth.month - 1] : '—'}
                  </p>
                </div>
              </div>
            }
          />
          <AreaTrend
            data={revenueMonths.map((m) => ({
              label: MONTHS[m.month - 1],
              sublabel: `${MONTHS[m.month - 1]} ${new Date().getFullYear()}`,
              value: m.value,
            }))}
            height={252}
            formatValue={(v) => moneyExact(v, cur)}
            formatTick={(v) => money(v, cur)}
            ariaLabel={`Revenue from paid appointments by month in ${new Date().getFullYear()}, ${moneyExact(revenueTotal, cur)} year to date.`}
          />
        </Card>

        {/* Appointments */}
        <Card className="@5xl:col-span-2">
          <CardTitle
            title="Appointments"
            subtitle="Booked visits and consultations"
            action={
              <Segmented
                value={apptRange}
                onChange={setApptRange}
                options={Object.entries(APPT_RANGES).map(([value, o]) => ({ value, label: o.label }))}
              />
            }
          />
          <div className="space-y-3">
            {todayAppts.length === 0 && (
              <EmptyState message={`No appointments ${APPT_RANGES[apptRange].label.toLowerCase()}.`} />
            )}
            {todayAppts.map((a, i) => (
              <Link key={a.id} to="/appointments"
                className="flex items-center gap-4 rounded-2xl bg-panel p-3.5 transition hover:bg-lavender">
                <Avatar name={a.customerName ?? '?'} tone={i % 2 ? 'mint' : 'cream'} />
                <div className="min-w-0 flex-1">
                  <p className="truncate text-[14px] font-bold">{a.serviceName ?? 'Appointment'}</p>
                  <p className="text-[12px] font-medium text-ink-soft">{fmtTime(a.startAt)} – {fmtTime(a.endAt)}</p>
                  <p className="truncate text-[11.5px] text-muted">{a.customerName} · {a.customerPhone}</p>
                  {a.serviceAddress && <p className="truncate text-[11.5px] text-muted">📍 {a.serviceAddress}</p>}
                </div>
                <Chip tone={statusTone(a.status)}>{a.status}</Chip>
              </Link>
            ))}
          </div>
          <Link to="/appointments">
            <PillButton className="mt-4 w-full py-3">+ New appointment</PillButton>
          </Link>
        </Card>

        {/* Busiest hours + agent status */}
        <div className="space-y-4 sm:space-y-5">
          <Card>
            <CardTitle title="Busiest hours" subtitle="When calls come in — last 30 days" />
            <HourHeat
              hours={Array.from({ length: 24 }, (_, h) => ({
                hour: h,
                value: (s.callsByHour ?? []).find((x) => x.hour === h)?.value ?? 0,
              }))}
              labelFor={hourLabel}
            />
          </Card>

          <Card>
            <CardTitle title="AI agent" subtitle="What the receptionist is working from" />
            <div className="space-y-3">
              <Link to="/settings" className="flex items-center gap-3 rounded-2xl bg-lavender p-3.5 transition hover:brightness-95">
                <span className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-ink text-sm text-on-ink">☎</span>
                <div className="min-w-0">
                  <p className="text-[13.5px] font-bold">Retell AI</p>
                  <p className="truncate text-[11.5px] text-ink-soft">
                    {retell?.connected
                      ? `Connected · ${retell.retellPhoneNumber ?? 'answering calls'}`
                      : retell?.apiKeyConfigured
                        ? 'Not connected — sync in Settings'
                        : 'API key missing'}
                  </p>
                </div>
              </Link>
              <Link to="/knowledge-base" className="flex items-center gap-3 rounded-2xl bg-mint p-3.5 transition hover:brightness-95">
                <span className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-ink text-sm text-on-ink">📚</span>
                <div>
                  <p className="text-[13.5px] font-bold">Knowledge base</p>
                  <p className="text-[11.5px] text-ink-soft">{kb?.length ?? 0} entries feeding the AI</p>
                </div>
              </Link>
              <Link to="/customers" className="flex items-center gap-3 rounded-2xl bg-cream p-3.5 transition hover:brightness-95">
                <span className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-ink text-sm text-on-ink">★</span>
                <div>
                  <p className="text-[13.5px] font-bold">Returning customers</p>
                  <p className="text-[11.5px] text-ink-soft">{s.returningCustomers ?? 0} have called more than once</p>
                </div>
              </Link>
            </div>
            <Link to="/settings">
              <PillButton variant="outline" className="mt-4 w-full py-2.5">Configure agent</PillButton>
            </Link>
          </Card>
        </div>
      </div>
    </div>
  )
}
