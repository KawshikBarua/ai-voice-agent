import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, unwrap } from '../api/client'
import { Card, Chip, EmptyState, statusTone } from '../components/ui'

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June',
  'July', 'August', 'September', 'October', 'November', 'December']

export default function CalendarPage() {
  const [cursor, setCursor] = useState(() => { const d = new Date(); return new Date(d.getFullYear(), d.getMonth(), 1) })
  const [selected, setSelected] = useState(null)

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
  const isToday = (d) => d === today.getDate() &&
    cursor.getMonth() === today.getMonth() && cursor.getFullYear() === today.getFullYear()

  const fmtTime = (iso) => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
  const selectedAppts = selected ? byDay[selected] ?? [] : []
  const selectedClosure = selected ? closureByDay[selected] : null

  return (
    <div>
      <div className="mb-6 flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-[26px] font-bold">Calendar</h1>
        <div className="flex items-center gap-2">
          <button onClick={() => setCursor(new Date(cursor.getFullYear(), cursor.getMonth() - 1, 1))}
            className="grid h-9 w-9 place-items-center rounded-full bg-card shadow-sm hover:bg-line">‹</button>
          <span className="min-w-40 text-center text-[15px] font-bold">
            {MONTHS[cursor.getMonth()]} {cursor.getFullYear()}
          </span>
          <button onClick={() => setCursor(new Date(cursor.getFullYear(), cursor.getMonth() + 1, 1))}
            className="grid h-9 w-9 place-items-center rounded-full bg-card shadow-sm hover:bg-line">›</button>
        </div>
      </div>

      <div className="grid gap-5 xl:grid-cols-[1fr_320px]">
        <Card>
          <div className="grid grid-cols-7 gap-2">
            {['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'].map((d) => (
              <span key={d} className="pb-2 text-center text-[11.5px] font-semibold text-muted">{d}</span>
            ))}
            {cells.map((d, i) => {
              const closure = d ? closureByDay[d] : null
              return (
                <button
                  key={i}
                  disabled={!d}
                  onClick={() => setSelected(d)}
                  title={closure ? `Closed — ${closure.name}` : undefined}
                  className={`min-h-20 rounded-2xl p-2 text-left transition ${
                    !d
                      ? 'bg-transparent'
                      : selected === d
                        ? 'bg-ink text-on-ink'
                        : closure
                          ? 'bg-cream hover:brightness-95'
                          : 'bg-panel hover:bg-line'
                  }`}
                >
                  {d && (
                    <>
                      <span className={`grid h-6 w-6 place-items-center rounded-full text-[12px] font-semibold ${
                        isToday(d) && selected !== d ? 'bg-ink text-on-ink' : ''
                      }`}>
                        {d}
                      </span>
                      {closure && (
                        <p className={`mt-1 truncate text-[10px] font-semibold leading-tight ${
                          selected === d ? 'text-on-ink/80' : 'text-ink-soft'
                        }`}>
                          {closure.name}
                        </p>
                      )}
                      {/* Appointments can still exist on a closure — booked before it was
                          marked, or added by staff — so the dots stay either way. */}
                      <div className="mt-1 flex flex-wrap gap-1">
                        {(byDay[d] ?? []).slice(0, 3).map((a) => (
                          <span key={a.id} className={`h-1.5 w-1.5 rounded-full ${
                            selected === d ? 'bg-accent' : 'bg-leaf'
                          }`} />
                        ))}
                      </div>
                    </>
                  )}
                </button>
              )
            })}
          </div>

          <div className="mt-4 flex flex-wrap items-center gap-x-5 gap-y-2 border-t border-line pt-3 text-[11.5px] text-muted">
            <span className="flex items-center gap-1.5">
              <span className="h-2.5 w-2.5 rounded-full bg-leaf" /> Appointment
            </span>
            <span className="flex items-center gap-1.5">
              <span className="h-2.5 w-2.5 rounded-[4px] bg-cream ring-1 ring-line" /> Closed
            </span>
          </div>
        </Card>

        <Card>
          <h2 className="mb-4 text-[17px] font-bold">
            {selected ? `${MONTHS[cursor.getMonth()]} ${selected}` : 'Select a day'}
          </h2>
          {selectedClosure && (
            <div className="mb-4 rounded-2xl bg-cream p-3.5">
              <p className="text-[13.5px] font-bold">Closed — {selectedClosure.name}</p>
              <p className="mt-1 text-[12px] text-ink-soft">
                The AI will not offer or book appointments on this date.
              </p>
            </div>
          )}
          {selected && selectedAppts.length === 0 && !selectedClosure &&
            <EmptyState message="No appointments on this day." />}
          <div className="space-y-3">
            {selectedAppts.map((a) => (
              <div key={a.id} className="rounded-2xl bg-lavender/70 p-3.5">
                <div className="flex items-center justify-between gap-2">
                  <p className="text-[13.5px] font-bold">{a.serviceName ?? 'Appointment'}</p>
                  <Chip tone={statusTone(a.status)}>{a.status}</Chip>
                </div>
                <p className="mt-1 text-[12px] text-ink-soft">
                  {fmtTime(a.startAt)} – {fmtTime(a.endAt)} · {a.customerName}
                </p>
              </div>
            ))}
          </div>
        </Card>
      </div>
    </div>
  )
}
