import { useEffect, useMemo, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate } from 'react-router-dom'
import { api, unwrap } from '../api/client'
import {
  Card, CardTitle, PillButton, Chip, Avatar, EmptyState, Segmented, Delta, statusTone,
} from '../components/ui'
import { StackedColumns, AreaTrend, GaugeMeter, BarMeter, Sparkline, HourHeat, Legend } from '../components/charts'
import {
  MONTHS, compact, money, moneyExact, minutes as fmtMinutes, duration, pctChange, whenLabel,
} from '../lib/format'
import { useUsage } from '../api/usage'
import { AttentionCard } from '../components/alerts'

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
// What the page renders when it has nothing.
//
// Nothing invented. This used to fall back to a generated month of plausible calls,
// bookings and revenue, labelled "sample figures" — but a dashboard is read at a
// glance, and a business owner glancing at 486 calls and $42,750 has been told
// something false about their own company. An account that is genuinely quiet, and
// an API that cannot be reached, both show real emptiness; the difference between
// them is said in words, above.
// ---------------------------------------------------------------------------
const NO_STATS = {
  currency: 'USD',
  callsPerMonth: [], bookingsPerMonth: [], revenuePerMonth: [],
  callOutcomesPerMonth: [], callsPerDay: [], callsByHour: [], upcomingHolidays: [],
}

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
        <p className={`text-xs font-medium ${featured ? 'opacity-70' : 'text-muted'}`}>{label}</p>
        {to && (
          <span aria-hidden="true" className={`grid h-6 w-6 shrink-0 place-items-center rounded-full text-xs ${
            featured ? 'bg-on-ink/15' : 'bg-panel text-ink-soft'
          }`}>↗</span>
        )}
      </div>
      <p className="mt-2 flex flex-wrap items-baseline gap-x-1.5">
        <span className="text-2xl font-bold leading-none sm:text-3xl">{value}</span>
        {unit && <span className={`text-sm font-medium ${featured ? 'opacity-70' : 'text-muted'}`}>{unit}</span>}
      </p>
      <div className="mt-3 flex-1">{children}</div>
      <div className="mt-3 min-h-[18px]">
        {delta !== undefined ? delta : (
          <p className={`text-xs ${featured ? 'opacity-70' : 'text-muted'}`}>{footnote}</p>
        )}
      </div>
    </div>
  )
  return to ? <Link to={to} className="block h-full">{body}</Link> : body
}

/* ---------------------------------------------------------------- call sync */

const clock = (s) => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`

const agoLabel = (iso) => {
  if (!iso) return 'not yet'
  const seconds = Math.max(0, Math.round((Date.now() - new Date(iso)) / 1000))
  if (seconds < 45) return 'just now'
  const mins = Math.round(seconds / 60)
  return mins < 60 ? `${mins} min ago` : `${Math.round(mins / 60)} h ago`
}

/**
 * When the agent's calls are next pulled in, counting down.
 *
 * Calls normally arrive by webhook the moment they end; this is the sweep that catches the ones
 * that did not, and until it runs those calls — and their minutes — are not on this page. So the
 * question "why does that number not move?" now has a visible answer with a time on it, rather
 * than a Sync button somebody has to know to press.
 *
 * Two details make it honest rather than decorative:
 *  - the server sends *seconds remaining*, not a timestamp, so a reader whose clock is wrong
 *    still sees the real interval;
 *  - reaching zero refreshes the figures it was counting down for, a few seconds later, once the
 *    pass has had time to finish. A countdown that ends and changes nothing on screen is worse
 *    than no countdown.
 */
function useCallSync() {
  const queryClient = useQueryClient()
  const [remaining, setRemaining] = useState(null)

  const { data, isFetching } = useQuery({
    queryKey: ['call-sync-status'],
    queryFn: () => api.get('/calls/sync/status').then(unwrap),
    // The schedule only changes when the API restarts; the countdown itself runs locally.
    refetchInterval: 10 * 60_000,
    // One attempt. The only realistic failure is an API that does not serve this route yet,
    // and retrying that produces nothing but a longer wait before the reader is told so.
    retry: false,
  })

  useEffect(() => {
    if (!data) return undefined
    const seconds = data.secondsUntilNext ?? 0

    // Counted against a deadline rather than by decrementing: a background tab's timers are
    // throttled to about once a minute, and a counter that only knows how many ticks it has
    // had comes back minutes behind.
    const deadline = Date.now() + seconds * 1000
    let refreshed = false

    const read = () => {
      const left = Math.max(0, Math.round((deadline - Date.now()) / 1000))
      setRemaining(left)

      if (left === 0 && seconds > 0 && !refreshed) {
        refreshed = true
        setTimeout(() => {
          for (const key of [['call-sync-status'], ['dashboard'], ['billing-usage'], ['calls']])
            queryClient.invalidateQueries({ queryKey: key })
        }, 4000)
      }
    }

    read()
    const id = setInterval(read, 1000)
    return () => clearInterval(id)
  }, [data, queryClient])

  const due = Boolean(data) && remaining === 0
  const every = data?.intervalSeconds ? Math.round(data.intervalSeconds / 60) : null

  // "We asked and got nothing back, and we are not still asking." Deliberately derived from the
  // absence of data rather than from `isError`: a request that is cancelled — a remount, a
  // navigation, React's double-invoke in development — leaves the query pending forever with no
  // error attached, and the first version of this sat on "Checking the schedule…" for good
  // because of it. This covers a 404, a cancellation and an outage with one boolean.
  const unavailable = !data && !isFetching

  return {
    due,
    unavailable,
    clock: !data ? '—' : due ? 'now' : clock(remaining ?? data.secondsUntilNext),
    detail: unavailable
      // Names the likely cause. This route is the newest thing here, so an API that predates it
      // is the common case — and "checking…" forever would hide that completely.
      ? 'Unavailable — the API may need restarting'
      : !data
        ? 'Checking the schedule…'
        : due
          ? 'Importing calls from Retell…'
          : `Synced ${agoLabel(data.lastRunAt)}${every ? ` · every ${every} min` : ''}`,
  }
}

/** The spinning-arrows glyph, spinning only while a pass is actually running. */
function SyncGlyph({ spinning, className = 'h-4 w-4' }) {
  return (
    <svg viewBox="0 0 24 24" className={`${className} ${spinning ? 'animate-spin' : ''}`} fill="none"
      stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
      <path d="M21 12a9 9 0 1 1-2.6-6.4" /><path d="M21 3v6h-6" />
    </svg>
  )
}

/**
 * The header's copy of the countdown — above the fold, where "next sync" is actually useful.
 * Hidden below sm, where the header is already two rows of controls and this is the least
 * important of them; the card row below carries the same figure for that case.
 */
function CallSyncChip() {
  const { due, unavailable, clock: value, detail } = useCallSync()
  if (unavailable) return null

  return (
    <span
      title={detail}
      className="hidden items-center gap-2 rounded-pill bg-card px-3.5 py-2.5 text-sm shadow-sm sm:inline-flex"
    >
      <SyncGlyph spinning={due} className="h-3.5 w-3.5 text-muted" />
      <span className="text-muted">Sync</span>
      <span className="font-semibold tabular-nums">{value}</span>
    </span>
  )
}

/** The same countdown as a full row, inside the AI agent card. */
function CallSyncRow() {
  const { due, clock: value, detail } = useCallSync()

  return (
    <div className="flex items-center gap-3 rounded-2xl bg-panel p-3.5">
      <span className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-ink text-sm text-on-ink">
        <SyncGlyph spinning={due} />
      </span>

      <div className="min-w-0 flex-1">
        <div className="flex items-baseline justify-between gap-2">
          <p className="text-base font-bold">Call sync</p>
          {/* Tabular figures: without them the countdown shifts sideways every second as the
              digits change width, which is the sort of thing that reads as cheap. */}
          <p className="shrink-0 text-base font-bold tabular-nums">{value}</p>
        </div>
        {/* Kept short enough to survive this card's width: it sits in the narrow column, and a
            sub-line that truncates mid-word looks broken rather than terse. */}
        <p className="mt-0.5 truncate text-xs text-ink-soft">{detail}</p>
      </div>
    </div>
  )
}

/** The four numbers under the call chart — the quality behind the volume. */
function MiniStat({ label, value, hint }) {
  return (
    <div className="rounded-2xl bg-panel px-3.5 py-3">
      <p className="text-xs font-medium text-muted">{label}</p>
      <p className="mt-1 text-lg font-bold leading-none">{value}</p>
      {hint && <p className="mt-1 text-2xs text-muted">{hint}</p>}
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

  const { data: stats, isError, isLoading, isPaused, refetch } = useQuery({
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

  // Minutes come from the billing endpoint rather than the stats blob, on a short poll, so the
  // allowance shown here is the same one the billing page shows and both move as calls land.
  const { data: usage } = useUsage()

  const s = stats ?? NO_STATS
  const cur = s.currency ?? 'USD'

  /*
    Why this is not just `isError`.

    React Query does not fail a query when the browser is offline — it *pauses* it. The request is
    never attempted, so status stays pending: isError is false, isLoading is false, and data is
    undefined. Keying the warning off isError alone therefore produced the one outcome this page
    must never have: a full screen of zeroes, with the standard heading above it, and nothing at
    all to say the figures had not been loaded. A reader would take that as "no calls yet".

    So the question asked is "is there anything to show", and the reason is reported separately.
  */
  const nothingToShow = !stats
  const offline = nothingToShow && isPaused
  const unreachable = nothingToShow && isError

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

  // Plan / talk time. The polled figures lead; the stats blob is the fallback for the first paint
  // and for a deployment where billing is not reachable.
  const included = usage?.includedMinutes ?? s.minutesIncluded ?? 0
  const usedPeriod = usage?.minutesUsed ?? s.minutesUsedThisPeriod ?? 0
  const remaining = Math.max(0, included - usedPeriod)
  const overBy = Math.max(0, usedPeriod - included)
  const periodEndRaw = usage?.periodEnd ?? s.periodEnd
  const periodEnd = periodEndRaw ? new Date(periodEndRaw) : null
  // The plan's name has to come from the same read as its minutes, or the card can end up saying
  // "No plan on file" directly above an allowance it is showing.
  const planName = (usage?.hasSubscription ? usage.planName : null) ?? s.planName
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
          <h1 className="font-display text-xl font-semibold leading-tight tracking-[-0.01em] sm:text-2xl">Dashboard</h1>
          <p className="mt-1 text-sm text-muted sm:text-sm">
            {offline
              ? 'You appear to be offline, so these figures could not be loaded.'
              : unreachable
                ? 'These figures could not be loaded.'
                : isLoading
                  ? 'Loading your figures…'
                  : 'How your AI receptionist performed.'}
          </p>
        </div>
        {/* Below sm the actions take the full row and share it evenly, rather than
            crowding into whatever space the title leaves. */}
        <div className="flex w-full flex-wrap items-center gap-2.5 sm:w-auto">
          {(offline || unreachable) && <Chip tone="red">{offline ? 'Offline' : 'Unavailable'}</Chip>}
          {/* When the agent's calls are next imported. Up here because the answer to "why has
              that number not moved?" is worth seeing without scrolling for it. */}
          <CallSyncChip />
          <form onSubmit={submitSearch}
            className="hidden items-center gap-2 rounded-pill bg-card px-4 py-2.5 shadow-sm lg:flex">
            <button type="submit" aria-label="Search customers" className="grid place-items-center">
              <svg viewBox="0 0 24 24" className="h-4 w-4 text-muted" fill="none" stroke="currentColor" strokeWidth="2">
                <circle cx="11" cy="11" r="7" /><path d="m21 21-4.3-4.3" />
              </svg>
            </button>
            <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search customers…"
              className="w-32 bg-transparent text-sm outline-none placeholder:text-muted xl:w-36" />
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

      {/*
        What happened while nobody was here, above everything else on the page. It renders only
        when something is actually waiting — a banner that is always present is one nobody reads,
        and this one has to still be noticed on the morning it matters.
      */}
      <AttentionCard />

      {/*
        Said plainly, and only when it is true. The tiles below are showing nothing because there
        is nothing to show them — never because something has been invented to fill them.
      */}
      {(offline || unreachable) && (
        <div className="mb-4 flex flex-wrap items-center justify-between gap-3 rounded-card bg-danger-soft px-4 py-3">
          <p className="text-sm font-medium text-danger">
            {offline
              ? 'You are offline, so nothing below is filled in.'
              : 'Your figures could not be loaded, so nothing below is filled in.'}{' '}
            No data has been lost — the zeroes are this page having nothing to show, not your
            account having nothing in it.
          </p>
          <PillButton variant="outline" onClick={() => refetch()}>Retry</PillButton>
        </div>
      )}

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
          <p className="text-xs text-muted">
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
              <table className="w-full min-w-[24rem] text-xs">
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
            subtitle={planName ? `${planName} plan · ${(s.billingCycle ?? 'Monthly').toLowerCase()}` : 'No plan on file'}
          />
          {included > 0 ? (
            <>
              <GaugeMeter used={usedPeriod} allowance={included}
                caption={`${fmtMinutes(usedPeriod)} of ${included.toLocaleString()} used`} />
              {overBy > 0 && (
                <p className="mt-2 rounded-2xl bg-danger-soft px-3.5 py-2.5 text-xs font-medium text-danger">
                  ⚠ {fmtMinutes(overBy)} over the allowance this period.
                </p>
              )}
            </>
          ) : (
            <GaugeMeter used={usedPeriod} allowance={0}
              caption="This plan is not metered on minutes" />
          )}

          <dl className="mt-4 space-y-2.5 text-sm">
            <div className="flex items-center justify-between gap-3">
              <dt className="text-muted">Used today</dt>
              <dd className="font-semibold tabular-nums">{fmtMinutes(s.minutesUsedToday ?? 0)}</dd>
            </div>
            <div className="flex items-center justify-between gap-3">
              <dt className="text-muted">{planName ? 'This period' : 'This month'}</dt>
              <dd className="font-semibold tabular-nums">{fmtMinutes(usedPeriod)}</dd>
            </div>
            <div className="flex items-center justify-between gap-3">
              <dt className="text-muted">All time</dt>
              <dd className="font-semibold tabular-nums">{fmtMinutes(s.minutesUsedTotal ?? 0)}</dd>
            </div>
            {/* Only meaningful with a plan behind it — without one the period is just
                the calendar month the usage above is counted over. */}
            {periodEnd && planName && (
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
                      <span className="block text-2xs font-semibold uppercase tracking-wide text-muted">
                        {MONTHS[date.getMonth()]}
                      </span>
                      <span className="block text-md font-bold">{date.getDate()}</span>
                    </div>
                    <div className="min-w-0 flex-1">
                      <p className="text-base font-bold leading-snug">{h.name}</p>
                      <p className="mt-0.5 text-xs text-ink-soft">
                        {date.toLocaleDateString(undefined, { weekday: 'long' })} · {whenLabel(h.daysAway)}
                      </p>
                      {h.appointmentsBooked > 0 && (
                        <p className="mt-0.5 text-xs font-semibold text-danger">
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
                  <p className="text-xs text-muted">Year to date</p>
                  <p className="text-md font-bold leading-tight sm:text-lg">{moneyExact(revenueTotal, cur)}</p>
                </div>
                <div className="text-right">
                  <p className="text-xs text-muted">Best month</p>
                  <p className="text-md font-bold leading-tight sm:text-lg">
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
                  <p className="truncate text-base font-bold">{a.serviceName ?? 'Appointment'}</p>
                  <p className="text-xs font-medium text-ink-soft">{fmtTime(a.startAt)} – {fmtTime(a.endAt)}</p>
                  <p className="truncate text-xs text-muted">{a.customerName} · {a.customerPhone}</p>
                  {a.serviceAddress && <p className="truncate text-xs text-muted">📍 {a.serviceAddress}</p>}
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
                  <p className="text-base font-bold">Retell AI</p>
                  <p className="truncate text-xs text-ink-soft">
                    {retell?.connected
                      ? `Connected · ${retell.retellPhoneNumber ?? 'answering calls'}`
                      : retell?.apiKeyConfigured
                        ? 'Not connected — sync in Settings'
                        : 'API key missing'}
                  </p>
                </div>
              </Link>
              {/* Directly under the connection it depends on: this is the other half of "are my
                  calls actually reaching me". */}
              <CallSyncRow />
              <Link to="/knowledge-base" className="flex items-center gap-3 rounded-2xl bg-mint p-3.5 transition hover:brightness-95">
                <span className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-ink text-sm text-on-ink">📚</span>
                <div>
                  <p className="text-base font-bold">Knowledge base</p>
                  <p className="text-xs text-ink-soft">{kb?.length ?? 0} entries feeding the AI</p>
                </div>
              </Link>
              <Link to="/customers" className="flex items-center gap-3 rounded-2xl bg-cream p-3.5 transition hover:brightness-95">
                <span className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-ink text-sm text-on-ink">★</span>
                <div>
                  <p className="text-base font-bold">Returning customers</p>
                  <p className="text-xs text-ink-soft">{s.returningCustomers ?? 0} have called more than once</p>
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
