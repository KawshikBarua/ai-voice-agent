import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, unwrap } from '../api/client'
import { Card, Chip, EmptyState, PillButton, compactAction, statusTone } from '../components/ui'

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June',
  'July', 'August', 'September', 'October', 'November', 'December']

/** Short and long forms of each weekday. A phone has room for one letter per column and no more;
 *  three-letter headings forced the columns wider than the cells beneath them. */
const WEEKDAYS = [['M', 'Mon'], ['T', 'Tue'], ['W', 'Wed'], ['T', 'Thu'],
  ['F', 'Fri'], ['S', 'Sat'], ['S', 'Sun']]

const startOfMonth = (d) => new Date(d.getFullYear(), d.getMonth(), 1)
const sameMonth = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth()

export default function CalendarPage() {
  const [cursor, setCursor] = useState(() => startOfMonth(new Date()))
  // Opening on today rather than on nothing: the day somebody wants when they tap Calendar is
  // almost always this one, and an empty panel below the grid looks like a page that failed.
  const [selected, setSelected] = useState(() => new Date().getDate())

  const monthStart = cursor
  const monthEnd = new Date(cursor.getFullYear(), cursor.getMonth() + 1, 1)

  const { data } = useQuery({
    queryKey: ['appointments', 'month', cursor.toISOString()],
    queryFn: () =>
      api.get('/appointments', {
        params: { from: monthStart.toISOString(), to: monthEnd.toISOString(), pageSize: 100 },
      }).then(unwrap),
  })

  // Shares the cache key with the Settings screen, so adding a closure there marks it here.
  const { data: holidays } = useQuery({
    queryKey: ['holidays'],
    queryFn: () => api.get('/settings/holidays').then(unwrap),
  })

  const byDay = useMemo(() => {
    const map = {}
    for (const a of data?.items ?? []) {
      const day = new Date(a.startAt).getDate()
      ;(map[day] ??= []).push(a)
    }
    // Earliest first: a day's list is read as a running order, not as a set.
    for (const list of Object.values(map)) list.sort((x, y) => new Date(x.startAt) - new Date(y.startAt))
    return map
  }, [data])

  /** Closures falling inside the month on screen, keyed by day number. */
  const closureByDay = useMemo(() => {
    const map = {}
    for (const h of holidays ?? []) {
      const d = new Date(h.date)
      if (d.getFullYear() === cursor.getFullYear() && d.getMonth() === cursor.getMonth())
        map[d.getDate()] = h
    }
    return map
  }, [holidays, cursor])

  const cells = useMemo(() => {
    const firstDow = (cursor.getDay() + 6) % 7
    const daysInMonth = new Date(cursor.getFullYear(), cursor.getMonth() + 1, 0).getDate()
    const out = Array(firstDow).fill(null)
    for (let d = 1; d <= daysInMonth; d++) out.push(d)
    while (out.length % 7 !== 0) out.push(null)
    return out
  }, [cursor])

  const today = new Date()
  const isToday = (d) => d === today.getDate() && sameMonth(cursor, today)

  /** Moving months lands on today when that is the month being shown, and on nothing otherwise —
   *  carrying a day number across months would select an unrelated date. */
  const goMonth = (delta) => {
    const next = new Date(cursor.getFullYear(), cursor.getMonth() + delta, 1)
    setCursor(next)
    setSelected(sameMonth(next, today) ? today.getDate() : null)
  }

  const goToday = () => {
    setCursor(startOfMonth(today))
    setSelected(today.getDate())
  }

  const fmtTime = (iso) => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
  const selectedAppts = selected ? byDay[selected] ?? [] : []
  const selectedClosure = selected ? closureByDay[selected] : null

  const navButton = 'grid h-9 w-9 shrink-0 place-items-center rounded-full bg-card text-lg leading-none shadow-sm transition hover:bg-line'

  return (
    <div>
      {/*
        One row on a desktop (title left, controls right) and two on a phone, where the month
        control needs the full width to itself — squeezed in beside the title it left about
        90px for the month name.
      */}
      <div className="mb-4 flex flex-col gap-3 sm:mb-6 sm:flex-row sm:items-center sm:justify-between">
        <h1 className="font-display text-xl font-semibold tracking-[-0.01em] sm:text-2xl">Calendar</h1>
        <div className="flex items-center gap-2">
          <button aria-label="Previous month" onClick={() => goMonth(-1)} className={navButton}>‹</button>
          <span className="flex-1 text-center text-md font-bold sm:min-w-40 sm:flex-none">
            {MONTHS[cursor.getMonth()]} {cursor.getFullYear()}
          </span>
          <button aria-label="Next month" onClick={() => goMonth(1)} className={navButton}>›</button>
          <PillButton variant="outline" className={compactAction} onClick={goToday}>Today</PillButton>
        </div>
      </div>

      <div className="grid gap-4 sm:gap-5 xl:grid-cols-[1fr_320px]">
        <Card>
          {/*
            Square cells, so the grid keeps the proportions of every month view a phone already
            shows. The old fixed 80px height made each day a tall narrow pill at 375px wide and
            pushed the day's own appointments two screens down.
          */}
          <div className="grid grid-cols-7 gap-1 sm:gap-2">
            {WEEKDAYS.map(([short, long], i) => (
              <span key={i} className="pb-1 text-center text-xs font-semibold text-muted sm:pb-2">
                <span className="sm:hidden">{short}</span>
                <span className="hidden sm:inline">{long}</span>
              </span>
            ))}
            {cells.map((d, i) => {
              const closure = d ? closureByDay[d] : null
              const appts = d ? byDay[d] ?? [] : []
              // d != null first: the padding cells at either end of the month are null, and so is
              // `selected` before a day is picked, so a bare equality painted them all as chosen.
              const isSelected = d != null && selected === d
              return (
                <button
                  key={i}
                  disabled={!d}
                  onClick={() => setSelected(d)}
                  aria-label={d ? `${MONTHS[cursor.getMonth()]} ${d}` : undefined}
                  aria-pressed={isSelected}
                  title={closure ? `Closed — ${closure.name}` : undefined}
                  className={`flex aspect-square flex-col items-center justify-center gap-1 rounded-xl transition sm:aspect-auto sm:block sm:min-h-20 sm:rounded-2xl sm:p-2 sm:text-left ${
                    !d
                      ? 'bg-transparent'
                      : isSelected
                        // Brand, not ink — "today" is already marked with an ink disc, so a
                        // selected day painted ink read as the same state at a glance.
                        ? 'bg-brand-strong text-on-brand'
                        : closure
                          ? 'bg-cream hover:brightness-95'
                          : 'bg-panel hover:bg-line'
                  }`}
                >
                  {d && (
                    <>
                      <span className={`grid h-7 w-7 place-items-center rounded-full text-sm font-semibold sm:h-6 sm:w-6 sm:text-xs ${
                        isToday(d) && !isSelected ? 'bg-ink text-on-ink' : ''
                      }`}>
                        {d}
                      </span>
                      {/* No room for a closure's name beside a 41px cell; the cream fill carries
                          it on a phone, and the legend below says what cream means. */}
                      {closure && (
                        <p className={`mt-1 hidden truncate text-2xs font-semibold leading-tight sm:block ${
                          isSelected ? 'text-on-brand/80' : 'text-ink-soft'
                        }`}>
                          {closure.name}
                        </p>
                      )}
                      {/* Appointments can still exist on a closure — booked before it was
                          marked, or added by staff — so the dots stay either way. */}
                      <div className="flex h-1.5 items-center justify-center gap-0.5 sm:mt-1 sm:h-auto sm:flex-wrap sm:justify-start sm:gap-1">
                        {appts.slice(0, 3).map((a) => (
                          <span key={a.id} className={`h-1.5 w-1.5 rounded-full ${
                            isSelected ? 'bg-on-brand/90' : 'bg-leaf'
                          }`} />
                        ))}
                      </div>
                    </>
                  )}
                </button>
              )
            })}
          </div>

          <div className="mt-4 flex flex-wrap items-center gap-x-5 gap-y-2 border-t border-line pt-3 text-xs text-muted">
            <span className="flex items-center gap-1.5">
              <span className="h-2.5 w-2.5 rounded-full bg-leaf" /> Appointment
            </span>
            <span className="flex items-center gap-1.5">
              <span className="h-2.5 w-2.5 rounded-[4px] bg-cream ring-1 ring-line" /> Closed
            </span>
          </div>
        </Card>

        <Card>
          <div className="mb-4 flex items-baseline justify-between gap-3">
            <h2 className="min-w-0 truncate font-display text-lg font-semibold tracking-[-0.01em]">
              {selected ? `${MONTHS[cursor.getMonth()]} ${selected}` : 'Select a day'}
            </h2>
            {selectedAppts.length > 0 && (
              <span className="shrink-0 text-xs text-muted">
                {selectedAppts.length} booked
              </span>
            )}
          </div>
          {selectedClosure && (
            <div className="mb-4 rounded-2xl bg-cream p-3.5">
              <p className="text-base font-bold">Closed — {selectedClosure.name}</p>
              <p className="mt-1 text-xs text-ink-soft">
                The AI will not offer or book appointments on this date.
              </p>
            </div>
          )}
          {!selected && <EmptyState message="Tap a day to see what is booked." />}
          {selected && selectedAppts.length === 0 && !selectedClosure &&
            <EmptyState message="No appointments on this day." />}
          <div className="space-y-2.5 sm:space-y-3">
            {selectedAppts.map((a) => (
              <div key={a.id} className="rounded-2xl bg-lavender/70 p-3.5">
                <div className="flex items-center justify-between gap-2">
                  <p className="min-w-0 truncate text-sm font-bold sm:text-base">
                    {a.serviceName ?? 'Appointment'}
                  </p>
                  <span className="shrink-0"><Chip tone={statusTone(a.status)}>{a.status}</Chip></span>
                </div>
                <p className="mt-1 truncate text-xs text-ink-soft">
                  <span className="font-semibold text-ink">{fmtTime(a.startAt)} – {fmtTime(a.endAt)}</span>
                  <span className="px-1.5 text-muted">·</span>
                  {a.customerName}
                </p>
              </div>
            ))}
          </div>
        </Card>
      </div>
    </div>
  )
}
