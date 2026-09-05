import { useEffect, useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { motion } from 'framer-motion'
import { useNavigate } from 'react-router-dom'
import { api, signOut, unwrap } from '../api/client'
import { useAuthStore } from '../store/auth'
import { useThemeStore } from '../store/theme'
import { Avatar, Chip, PillButton } from '../components/ui'
import {
  currentSubscription, disablePush, enablePush, iosNeedsInstall, permission, pushSupported,
} from '../lib/push'

/* ------------------------------------------------------------------ primitives */

// min-w-0 is load-bearing, not tidiness: inputs (dates and times especially) have an intrinsic
// width, and as grid/flex children their default min-width:auto lets that intrinsic width push
// the page wider than the screen instead of the field shrinking to fit.
const inputBase =
  'w-full min-w-0 rounded-2xl border px-4 py-2.5 text-base outline-none transition-colors'

/**
 * One labelled control. Locked fields keep the same shape as editable ones so the card does
 * not reflow when Edit is pressed — only the border and background change.
 */
function Field({ label, children, full = false, hint }) {
  return (
    <div className={full ? 'sm:col-span-2' : ''}>
      <label className="mb-1.5 block text-xs font-semibold text-ink-soft">{label}</label>
      {children}
      {hint && <p className="mt-1.5 text-xs text-muted">{hint}</p>}
    </div>
  )
}

function TextInput({ locked, className = '', ...rest }) {
  return (
    <input
      disabled={locked}
      className={`${inputBase} ${
        locked
          ? 'border-transparent bg-panel text-ink-soft'
          : 'border-line bg-card text-ink focus:border-ink'
      } ${className}`}
      {...rest}
    />
  )
}

function SelectInput({ locked, children, ...rest }) {
  return (
    <select
      disabled={locked}
      className={`${inputBase} ${
        locked ? 'border-transparent bg-panel text-ink-soft' : 'border-line bg-card text-ink focus:border-ink'
      }`}
      {...rest}
    >
      {children}
    </select>
  )
}

function TextArea({ locked, className = '', ...rest }) {
  return (
    <textarea
      disabled={locked}
      className={`${inputBase} ${
        locked ? 'border-transparent bg-panel text-ink-soft' : 'border-line bg-card text-ink focus:border-ink'
      } ${className}`}
      {...rest}
    />
  )
}

const PencilIcon = () => (
  <svg viewBox="0 0 24 24" className="h-3.5 w-3.5" fill="none" stroke="currentColor"
    strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
    <path d="M12 20h9" />
    <path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4z" />
  </svg>
)

/**
 * A settings card that is read-only until Edit is pressed. Locking by default is what stops
 * a stray keystroke on a page full of inputs from silently changing the business the AI
 * quotes prices from.
 */
function EditableCard({ title, subtitle, editing, onEdit, onCancel, onSave, saving, error, children, footer }) {
  return (
    <motion.section
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.28, ease: 'easeOut' }}
      className="theme-fade rounded-card border border-line bg-card p-4 shadow-sm sm:p-5"
    >
      <div className="mb-5 flex items-start justify-between gap-3">
        <div>
          <h2 className="text-md font-bold">{title}</h2>
          {subtitle && <p className="mt-0.5 text-xs text-muted">{subtitle}</p>}
        </div>

        {onEdit && !editing && (
          <button
            onClick={onEdit}
            className="inline-flex shrink-0 items-center gap-1.5 rounded-xl border border-line bg-card px-3 py-1.5 text-sm font-semibold text-ink-soft transition hover:bg-panel"
          >
            Edit <PencilIcon />
          </button>
        )}
      </div>

      <div className="grid gap-4 sm:grid-cols-2">{children}</div>

      {error && (
        <p className="mt-4 rounded-2xl bg-danger-soft px-4 py-2.5 text-sm font-medium text-danger">
          {error}
        </p>
      )}

      {editing && (
        <div className="mt-5 grid gap-2.5 sm:flex sm:items-center">
          <PillButton onClick={onSave} disabled={saving}>
            {saving ? 'Saving…' : 'Save changes'}
          </PillButton>
          <PillButton variant="outline" onClick={onCancel} disabled={saving}>
            Cancel
          </PillButton>
        </div>
      )}

      {footer}
    </motion.section>
  )
}

function CardState({ title, isPending, error }) {
  const status = error?.response?.status
  const message = isPending
    ? 'Loading…'
    : status === 401
      ? 'Your session has expired. Please sign in again.'
      : status === 403
        ? 'You do not have permission to view this section.'
        : error?.response?.data?.message ?? 'Could not load this section. Please retry.'

  return (
    <section className="theme-fade rounded-card border border-line bg-card p-4 shadow-sm sm:p-5">
      <h2 className="text-md font-bold">{title}</h2>
      <p className={`mt-2 text-sm ${isPending ? 'text-muted' : 'text-danger'}`}>{message}</p>
    </section>
  )
}

/** Small helper so a mutation's message survives both envelope and network failures. */
const errorText = (mutation, fallback) =>
  mutation.isError
    ? mutation.error?.response?.data?.message ?? fallback
    : null

/* ------------------------------------------------------------- business hours */

/** Sunday-first, matching BusinessHours.DayKeys on the server. */
const DAY_KEYS = ['sun', 'mon', 'tue', 'wed', 'thu', 'fri', 'sat']

/** Monday-first, the order a business states its hours in. */
const WEEK = [
  { key: 'mon', label: 'Monday' },
  { key: 'tue', label: 'Tuesday' },
  { key: 'wed', label: 'Wednesday' },
  { key: 'thu', label: 'Thursday' },
  { key: 'fri', label: 'Friday' },
  { key: 'sat', label: 'Saturday' },
  { key: 'sun', label: 'Sunday' },
]

const DEFAULT_START = '09:00'
const DEFAULT_END = '17:00'

/** Matches a stored key — a single day ('mon') or a range ('mon-fri', or wrapped 'fri-mon'). */
function keyCoversDay(storedKey, dayKey) {
  const key = storedKey.trim().toLowerCase()
  if (key === dayKey) return true
  const ends = key.split('-')
  if (ends.length !== 2) return false
  const a = DAY_KEYS.indexOf(ends[0].trim())
  const b = DAY_KEYS.indexOf(ends[1].trim())
  const d = DAY_KEYS.indexOf(dayKey)
  if (a < 0 || b < 0 || d < 0) return false
  return a <= b ? d >= a && d <= b : d >= a || d <= b
}

const isClockTime = (value) => /^\d{2}:\d{2}$/.test(value)

/**
 * Reads BusinessHoursJson exactly the way BusinessHours.For does on the server
 * (backend/AiReceptionist.Api/Common/TenantTime.cs): no config at all means 09:00–17:00 every
 * day, but a config that omits a day means that day is closed. Diverging here would show the
 * owner one schedule while the AI booked callers into another.
 */
function parseBusinessHours(json) {
  let config = null
  if (json) {
    try {
      const parsed = JSON.parse(json)
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) config = parsed
    } catch {
      config = null // unreadable config falls back to the same default the server uses
    }
  }

  return WEEK.map((day) => {
    const base = { ...day, start: DEFAULT_START, end: DEFAULT_END }
    if (!config) return { ...base, open: true }

    const entry = Object.entries(config).find(([key]) => keyCoversDay(key, day.key))
    if (!entry) return { ...base, open: false }

    const value = String(entry[1] ?? '').trim()
    if (!value || value.toLowerCase() === 'closed') return { ...base, open: false }

    const [start, end] = value.split('-').map((part) => part?.trim() ?? '')
    return isClockTime(start) && isClockTime(end) && end > start
      ? { ...base, open: true, start, end }
      : { ...base, open: true }
  })
}

/** Always writes all seven single-day keys, so a round-trip through the editor is stable. */
const serializeBusinessHours = (rows) =>
  JSON.stringify(Object.fromEntries(
    rows.map((row) => [row.key, row.open ? `${row.start}-${row.end}` : 'closed']),
  ))

const invalidHourRows = (rows) =>
  rows.filter((row) => row.open && !(isClockTime(row.start) && isClockTime(row.end) && row.end > row.start))

/**
 * One day's hours.
 *
 * A day name, a checkbox and two time pickers do not fit across a phone, and left to wrap they
 * broke differently depending on the length of the weekday — seven rows, no two aligned. So below
 * sm the row becomes two lines (day and open-state, then the times) and the times take the full
 * width, which also makes them far easier to hit. `sm:contents` dissolves the first line's
 * wrapper at larger widths so all four parts sit on the single row they always did.
 */
function HoursRow({ row, editing, onChange }) {
  const timeInput = `min-w-0 flex-1 rounded-xl border px-3 py-2 text-sm outline-none transition-colors sm:flex-none sm:py-1.5 ${
    editing ? 'border-line bg-card text-ink focus:border-ink' : 'border-transparent bg-card/60 text-ink-soft'
  }`

  return (
    <div className="flex flex-col gap-2 rounded-2xl bg-panel px-3.5 py-2.5 sm:flex-row sm:flex-wrap sm:items-center sm:gap-x-4 sm:gap-y-2 sm:px-4">
      <div className="flex items-center justify-between gap-3 sm:contents">
        <span className="text-sm font-semibold sm:w-[92px] sm:shrink-0">{row.label}</span>

        <label className="flex shrink-0 items-center gap-2 text-sm text-ink-soft">
          <input
            type="checkbox"
            className="h-4 w-4 rounded accent-ink"
            checked={row.open}
            disabled={!editing}
            onChange={(e) => onChange({ ...row, open: e.target.checked })}
          />
          Open
        </label>
      </div>

      {row.open ? (
        <div className="flex items-center gap-2">
          <input type="time" className={timeInput} value={row.start} disabled={!editing}
            onChange={(e) => onChange({ ...row, start: e.target.value })} aria-label={`${row.label} opening time`} />
          <span className="shrink-0 text-sm text-muted">to</span>
          <input type="time" className={timeInput} value={row.end} disabled={!editing}
            onChange={(e) => onChange({ ...row, end: e.target.value })} aria-label={`${row.label} closing time`} />
        </div>
      ) : (
        <span className="text-sm font-medium text-muted">Closed all day</span>
      )}
    </div>
  )
}

/* ---------------------------------------------------------- holiday closures */

const todayStart = () => { const d = new Date(); d.setHours(0, 0, 0, 0); return d }

const formatClosureDate = (value) =>
  new Date(value).toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' })

/**
 * Dates the business is shut. These override the weekly hours for that day: the agent refuses
 * to offer or book a slot on them, and states the reason to the caller.
 */
function HolidaysCard() {
  const qc = useQueryClient()
  const { data: holidays, isPending, error } = useQuery({
    queryKey: ['holidays'],
    queryFn: () => api.get('/settings/holidays').then(unwrap),
  })

  const [date, setDate] = useState('')
  const [name, setName] = useState('')

  // The prompt preview on the Knowledge Base screen embeds these, so it goes stale otherwise.
  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['holidays'] })
    qc.invalidateQueries({ queryKey: ['final-prompt'] })
  }

  const add = useMutation({
    mutationFn: (body) => api.post('/settings/holidays', body).then(unwrap),
    onSuccess: () => { refresh(); setDate(''); setName('') },
  })
  const remove = useMutation({
    mutationFn: (id) => api.delete(`/settings/holidays/${id}`),
    onSuccess: refresh,
  })

  if (isPending || error)
    return <CardState title="Holidays & Closures" isPending={isPending} error={error} />

  const addError = errorText(add, 'Could not add that closure. Please retry.')
  const today = todayStart()
  const field = 'min-w-0 rounded-2xl border border-line bg-card px-4 py-2.5 text-base outline-none focus:border-ink'

  return (
    <motion.section
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.28, ease: 'easeOut' }}
      className="theme-fade rounded-card border border-line bg-card p-4 shadow-sm sm:p-5"
    >
      <h2 className="text-md font-bold">Holidays &amp; Closures</h2>
      <p className="mt-0.5 text-xs text-muted">
        Days the business is shut. These override your weekly hours — the AI will not book
        anyone in, and tells callers why.
      </p>

      {/* Stacked and full-width on a phone; the same single line as before from sm. */}
      <form
        className="mt-4 grid gap-2.5 sm:flex sm:flex-wrap sm:items-end"
        onSubmit={(e) => { e.preventDefault(); add.mutate({ date, name: name.trim() || 'Closed' }) }}
      >
        <div>
          <label className="mb-1.5 block text-xs font-semibold text-ink-soft" htmlFor="closure-date">Date</label>
          <input id="closure-date" type="date" required className={`${field} w-full sm:w-auto`} value={date}
            onChange={(e) => setDate(e.target.value)} />
        </div>
        <div className="sm:min-w-[180px] sm:flex-1">
          <label className="mb-1.5 block text-xs font-semibold text-ink-soft" htmlFor="closure-name">Reason</label>
          <input id="closure-name" className={`${field} w-full`} value={name} placeholder="e.g. Christmas Day"
            onChange={(e) => setName(e.target.value)} />
        </div>
        <PillButton type="submit" className="w-full sm:w-auto" disabled={add.isPending || !date}>
          {add.isPending ? 'Adding…' : 'Add closure'}
        </PillButton>
      </form>

      {addError && (
        <p className="mt-4 rounded-2xl bg-danger-soft px-4 py-2.5 text-sm font-medium text-danger">
          {addError}
        </p>
      )}

      <div className="mt-5 space-y-2">
        {holidays.length === 0 && (
          <p className="rounded-2xl bg-panel px-4 py-3 text-sm text-muted">
            No closures yet. Your weekly hours apply on every date.
          </p>
        )}
        {holidays.map((h) => {
          const past = new Date(h.date) < today
          return (
            <div key={h.id}
              className={`flex flex-wrap items-center gap-x-3 gap-y-1 rounded-2xl bg-panel px-4 py-2.5 ${past ? 'opacity-60' : ''}`}>
              {/* Full width below sm, so the date gets its own line and the reason keeps a whole
                  line to itself rather than being truncated to two words. */}
              <span className="w-full text-sm font-semibold sm:w-[150px] sm:shrink-0">{formatClosureDate(h.date)}</span>
              <span className="min-w-0 flex-1 truncate text-sm text-ink-soft">{h.name}</span>
              {past && <Chip tone="cream">Past</Chip>}
              <button
                onClick={() => remove.mutate(h.id)}
                disabled={remove.isPending}
                className="rounded-pill px-3 py-1 text-xs font-semibold text-danger transition hover:bg-danger-soft disabled:opacity-50"
              >
                Remove
              </button>
            </div>
          )
        })}
      </div>
    </motion.section>
  )
}

/* ------------------------------------------------------------------- team */

/**
 * Seeds a rota editor. An employee with no hours of their own works the business hours, so that
 * is what the editor starts from the moment someone gives them their own — an empty week would
 * make "same as the business, but not Saturdays" a seven-row chore.
 */
const seedRota = (employee, businessHoursJson) =>
  parseBusinessHours(employee.workingHoursJson ?? businessHoursJson)

const formatDay = (value) =>
  new Date(value).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })

const formatAway = (t) =>
  t.startDate === t.endDate || formatDay(t.startDate) === formatDay(t.endDate)
    ? formatDay(t.startDate)
    : `${formatDay(t.startDate)} – ${formatDay(t.endDate)}`

/**
 * One person's weekly rota. The default — no hours of their own — is the common case and stays a
 * single checkbox; the seven rows only appear for someone who genuinely differs from the business.
 */
function RotaEditor({ value, businessHoursJson, onChange }) {
  const custom = value != null
  const rows = parseBusinessHours(custom ? value : businessHoursJson)

  return (
    <div className="space-y-2 sm:col-span-2">
      <label className="flex items-center gap-2 text-sm font-semibold text-ink-soft">
        <input
          type="checkbox"
          className="h-4 w-4 rounded accent-ink"
          checked={!custom}
          onChange={(e) => onChange(e.target.checked ? null : serializeBusinessHours(rows))}
        />
        Works whenever the business is open
      </label>

      {custom && rows.map((row, i) => (
        <HoursRow
          key={row.key}
          row={row}
          editing
          onChange={(next) => onChange(serializeBusinessHours(rows.map((r, j) => (j === i ? next : r))))}
        />
      ))}

      <p className="px-1 text-xs text-muted">
        {custom
          ? 'The AI books this person only inside these hours, and never outside the business hours.'
          : 'Give someone their own hours when they work part of the week — a Saturday-only stylist, an early shift.'}
      </p>
    </div>
  )
}

/** Days one person is away. Everyone else stays bookable — that is the point of a team. */
function TimeOffEditor({ employee, entries, onAdd, onRemove, adding, error }) {
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [reason, setReason] = useState('')

  const field = 'min-w-0 rounded-2xl border border-line bg-card px-3 py-2 text-sm outline-none focus:border-ink'

  return (
    <div className="sm:col-span-2">
      <h4 className="text-xs font-semibold text-ink-soft">Time off</h4>

      {/* The two dates share a line even on the narrowest phone — they are a pair, and reading
          "first day" directly above "last day" is what makes the range obvious. */}
      <form
        className="mt-2 grid gap-2 sm:flex sm:flex-wrap sm:items-end"
        onSubmit={(e) => {
          e.preventDefault()
          onAdd({ startDate: from, endDate: to || from, reason: reason.trim() || null })
          setFrom(''); setTo(''); setReason('')
        }}
      >
        <div className="grid grid-cols-2 gap-2 sm:contents">
        <div className="min-w-0">
          <label className="mb-1 block text-[11px] font-semibold text-muted" htmlFor={`off-from-${employee.id}`}>First day</label>
          <input id={`off-from-${employee.id}`} type="date" required className={`${field} w-full`} value={from}
            onChange={(e) => setFrom(e.target.value)} />
        </div>
        <div className="min-w-0">
          <label className="mb-1 block text-[11px] font-semibold text-muted" htmlFor={`off-to-${employee.id}`}>Last day</label>
          <input id={`off-to-${employee.id}`} type="date" className={`${field} w-full`} value={to} min={from}
            onChange={(e) => setTo(e.target.value)} />
        </div>
        </div>
        <div className="sm:min-w-[150px] sm:flex-1">
          <label className="mb-1 block text-[11px] font-semibold text-muted" htmlFor={`off-why-${employee.id}`}>Reason</label>
          <input id={`off-why-${employee.id}`} className={`${field} w-full`} value={reason} placeholder="Holiday"
            onChange={(e) => setReason(e.target.value)} />
        </div>
        <PillButton type="submit" variant="outline" className="w-full sm:w-auto" disabled={adding || !from}>
          {adding ? 'Saving…' : 'Add time off'}
        </PillButton>
      </form>

      {error && <p className="mt-2 text-xs font-medium text-danger">{error}</p>}

      <div className="mt-3 space-y-1.5">
        {entries.length === 0 && <p className="text-xs text-muted">No time off booked.</p>}
        {entries.map((t) => (
          <div key={t.id} className="flex flex-wrap items-center gap-x-2 gap-y-1 rounded-xl bg-card px-3 py-2">
            {/* A date range is ~200px of unbreakable text; on a narrow phone it takes the line to
                itself so the reason beside it is not squeezed down to nothing. */}
            <span className="w-full text-xs font-semibold sm:w-auto">{formatAway(t)}</span>
            <span className="min-w-0 flex-1 truncate text-xs text-muted">{t.reason ?? 'Away'}</span>
            <button
              onClick={() => onRemove(t.id)}
              className="rounded-pill px-2.5 py-1 text-[11px] font-semibold text-danger transition hover:bg-danger-soft"
            >
              Remove
            </button>
          </div>
        ))}
      </div>
    </div>
  )
}

/** One row of the roster, expanding into the full editor for that person. */
function TeamMember({ employee, timeOff, businessHoursJson, open, onToggle, save, remove, addTimeOff, removeTimeOff }) {
  const [form, setForm] = useState(employee)

  // Re-seeded whenever the row reopens, so cancelling by collapsing leaves nothing half-edited.
  useEffect(() => { if (open) setForm(employee) }, [open, employee])

  const set = (k) => (e) => setForm({ ...form, [k]: e.target.value })
  const rota = parseBusinessHours(form.workingHoursJson ?? businessHoursJson)
  const badRows = invalidHourRows(rota)
  const rotaError = form.workingHoursJson != null && badRows.length > 0
    ? `Finish time must be after start time on ${badRows.map((r) => r.label).join(', ')}.`
    : null

  // Rendered in two places — beside the name on a wide screen, beneath it on a phone — so the
  // conditions live here rather than being written out twice.
  const statusChips = (
    <>
      {!employee.isActive && <Chip tone="cream">Not taking bookings</Chip>}
      {employee.upcomingAppointments > 0 && (
        <Chip tone="lavender">{employee.upcomingAppointments} upcoming</Chip>
      )}
    </>
  )

  return (
    <div className="rounded-2xl bg-panel">
      <div className="flex items-center gap-3 px-3.5 py-3 sm:px-4">
        <Avatar name={employee.name} size="sm" tone={employee.isActive ? 'mint' : 'cream'} />
        <div className="min-w-0 flex-1">
          <p className="truncate text-sm font-bold">{employee.name}</p>
          <p className="truncate text-xs text-muted">
            {employee.jobTitle || 'Team member'}
            {employee.workingHoursJson ? ' · own hours' : ''}
          </p>
          {/* Below the name on a phone, where there is no room beside it — pushing the chips
              onto the end of the row squeezed the name down to a couple of characters. */}
          <div className="mt-1.5 flex flex-wrap gap-1.5 sm:hidden">{statusChips}</div>
        </div>

        <div className="hidden shrink-0 items-center gap-2 sm:flex">{statusChips}</div>

        <button
          onClick={onToggle}
          className="shrink-0 rounded-pill border border-line bg-card px-3 py-1.5 text-xs font-semibold text-ink-soft transition hover:bg-panel"
        >
          {open ? 'Close' : 'Edit'}
        </button>
      </div>

      {open && (
        <div className="grid gap-4 border-t border-line px-3.5 py-4 sm:grid-cols-2 sm:px-4">
          <Field label="Name">
            <TextInput value={form.name ?? ''} onChange={set('name')} />
          </Field>
          <Field label="Job title">
            <TextInput value={form.jobTitle ?? ''} onChange={set('jobTitle')} placeholder="Stylist, Technician…" />
          </Field>
          <Field label="Phone">
            <TextInput value={form.phone ?? ''} onChange={set('phone')} />
          </Field>
          <Field label="Email">
            <TextInput value={form.email ?? ''} onChange={set('email')} />
          </Field>

          <div className="sm:col-span-2">
            <label className="flex items-center gap-2 text-sm font-semibold text-ink-soft">
              <input
                type="checkbox"
                className="h-4 w-4 rounded accent-ink"
                checked={form.isActive}
                onChange={(e) => setForm({ ...form, isActive: e.target.checked })}
              />
              Taking bookings
            </label>
            <p className="mt-1 text-xs text-muted">
              Turn this off and the AI stops booking them, while everything already in the diary stands.
            </p>
          </div>

          <RotaEditor
            value={form.workingHoursJson ?? null}
            businessHoursJson={businessHoursJson}
            onChange={(next) => setForm({ ...form, workingHoursJson: next })}
          />

          {(rotaError || save.error || remove.error) && (
            <p className="rounded-2xl bg-danger-soft px-4 py-2.5 text-sm font-medium text-danger sm:col-span-2">
              {rotaError ??
                (remove.isError
                  ? remove.error?.response?.data?.message ?? 'Could not remove them. Please retry.'
                  : save.error?.response?.data?.message ?? 'Could not save. Please retry.')}
            </p>
          )}

          <div className="grid gap-2 sm:col-span-2 sm:flex sm:flex-wrap">
            <PillButton
              disabled={save.isPending || !form.name?.trim() || rotaError != null}
              onClick={() => save.mutate(form)}
            >
              {save.isPending ? 'Saving…' : 'Save'}
            </PillButton>
            <PillButton variant="outline" onClick={onToggle}>Cancel</PillButton>
            <PillButton
              variant="light"
              className="!text-danger"
              disabled={remove.isPending}
              onClick={() => remove.mutate(employee.id)}
            >
              {remove.isPending ? 'Removing…' : 'Remove'}
            </PillButton>
          </div>

          <div className="border-t border-line pt-4 sm:col-span-2">
            <TimeOffEditor
              employee={employee}
              entries={timeOff}
              adding={addTimeOff.isPending}
              error={errorText(addTimeOff, 'Could not save that time off. Please retry.')}
              onAdd={(body) => addTimeOff.mutate({ id: employee.id, body })}
              onRemove={(id) => removeTimeOff.mutate(id)}
            />
          </div>
        </div>
      )}
    </div>
  )
}

/**
 * The roster the AI books against.
 *
 * This is what decides how many appointments can run at once: the agent offers a time while at
 * least one person is on duty and free for it, so two people mean two callers can both have noon
 * and an empty rota means neither can.
 */
function TeamPanel() {
  const qc = useQueryClient()

  const { data: org } = useQuery({
    queryKey: ['organization'],
    queryFn: () => api.get('/settings/organization').then(unwrap),
  })
  const { data: employees, isPending, error } = useQuery({
    queryKey: ['employees'],
    queryFn: () => api.get('/employees').then(unwrap),
  })
  const { data: timeOff } = useQuery({
    queryKey: ['employee-time-off'],
    queryFn: () => api.get('/employees/time-off').then(unwrap),
  })

  const [openId, setOpenId] = useState(null)
  const [newName, setNewName] = useState('')
  const [newTitle, setNewTitle] = useState('')

  // The prompt states the size of the team, so its preview goes stale otherwise.
  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['employees'] })
    qc.invalidateQueries({ queryKey: ['employee-time-off'] })
    qc.invalidateQueries({ queryKey: ['final-prompt'] })
  }

  const add = useMutation({
    mutationFn: (body) => api.post('/employees', body).then(unwrap),
    onSuccess: () => { refresh(); setNewName(''); setNewTitle('') },
  })
  const save = useMutation({
    mutationFn: (body) => api.put(`/employees/${body.id}`, body).then(unwrap),
    onSuccess: () => { refresh(); setOpenId(null) },
  })
  const remove = useMutation({
    mutationFn: (id) => api.delete(`/employees/${id}`),
    onSuccess: () => { refresh(); setOpenId(null) },
  })
  const addTimeOff = useMutation({
    mutationFn: ({ id, body }) => api.post(`/employees/${id}/time-off`, body).then(unwrap),
    onSuccess: refresh,
  })
  const removeTimeOff = useMutation({
    mutationFn: (id) => api.delete(`/employees/time-off/${id}`),
    onSuccess: refresh,
  })

  if (isPending || error) return <CardState title="Team" isPending={isPending} error={error} />

  const active = employees.filter((e) => e.isActive).length
  const field = 'min-w-0 rounded-2xl border border-line bg-card px-4 py-2.5 text-base outline-none focus:border-ink'

  return (
    <div className="space-y-4">
      <motion.section
        initial={{ opacity: 0, y: 8 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.28, ease: 'easeOut' }}
        className="theme-fade rounded-card border border-line bg-card p-4 shadow-sm sm:p-5"
      >
        <h2 className="text-md font-bold">Team</h2>
        <p className="mt-0.5 text-xs text-muted">
          Who takes appointments. The AI can book one caller with each person at the same time —
          two people means two callers can both have noon.
        </p>

        <form
          className="mt-4 grid gap-2.5 sm:flex sm:flex-wrap sm:items-end"
          onSubmit={(e) => {
            e.preventDefault()
            add.mutate({ name: newName.trim(), jobTitle: newTitle.trim() || null, isActive: true })
          }}
        >
          <div className="sm:min-w-[160px] sm:flex-1">
            <label className="mb-1.5 block text-xs font-semibold text-ink-soft" htmlFor="new-employee">Name</label>
            <input id="new-employee" required className={`${field} w-full`} value={newName} placeholder="e.g. James"
              onChange={(e) => setNewName(e.target.value)} />
          </div>
          <div className="sm:min-w-[160px] sm:flex-1">
            <label className="mb-1.5 block text-xs font-semibold text-ink-soft" htmlFor="new-employee-title">Job title</label>
            <input id="new-employee-title" className={`${field} w-full`} value={newTitle} placeholder="Optional"
              onChange={(e) => setNewTitle(e.target.value)} />
          </div>
          <PillButton type="submit" className="w-full sm:w-auto" disabled={add.isPending || !newName.trim()}>
            {add.isPending ? 'Adding…' : 'Add person'}
          </PillButton>
        </form>

        {add.isError && (
          <p className="mt-4 rounded-2xl bg-danger-soft px-4 py-2.5 text-sm font-medium text-danger">
            {errorText(add, 'Could not add them. Please retry.')}
          </p>
        )}

        <div className="mt-5 space-y-2">
          {employees.length === 0 && (
            <p className="rounded-2xl bg-panel px-4 py-3 text-sm text-muted">
              No one on the team yet. Until someone is added the AI books one appointment at a time.
            </p>
          )}

          {employees.map((employee) => (
            <TeamMember
              key={employee.id}
              employee={employee}
              businessHoursJson={org?.businessHoursJson}
              timeOff={(timeOff ?? []).filter((t) => t.employeeId === employee.id)}
              open={openId === employee.id}
              onToggle={() => {
                save.reset(); remove.reset(); addTimeOff.reset()
                setOpenId(openId === employee.id ? null : employee.id)
              }}
              save={save}
              remove={remove}
              addTimeOff={addTimeOff}
              removeTimeOff={removeTimeOff}
            />
          ))}
        </div>

        {employees.length > 0 && (
          <p className="mt-4 px-1 text-xs text-muted">
            {active === 0
              ? 'Nobody is taking bookings, so the AI will not book anyone in. Mark at least one person as taking bookings.'
              : `Up to ${active} appointment${active === 1 ? '' : 's'} can run at the same time, fewer on days ` +
                'when someone is off. The AI works this out for itself on every call.'}
          </p>
        )}
      </motion.section>
    </div>
  )
}

/* ---------------------------------------------------------- notifications */

/**
 * Turning this device into something the AI can reach.
 *
 * The problem it solves is the one nobody notices until it costs them: an owner is not sitting in
 * this dashboard, so a booking taken at 2am — or an emergency taken during a haircut — is
 * invisible until they next happen to sign in. A browser notification reaches the phone with the
 * app closed and costs nothing to send.
 *
 * Every state below is a real thing a person hits, and each one says what to do about it rather
 * than just reporting that it is off.
 */
function NotificationsPanel() {
  const qc = useQueryClient()

  const { data: config, isPending, error } = useQuery({
    queryKey: ['push-config'],
    queryFn: () => api.get('/notifications/config').then(unwrap),
    staleTime: Infinity,
  })
  const { data: devices } = useQuery({
    queryKey: ['push-devices'],
    queryFn: () => api.get('/notifications/devices').then(unwrap),
  })

  // What this browser is doing, as opposed to what the account has registered elsewhere.
  const [endpoint, setEndpoint] = useState(null)
  const [state, setState] = useState('checking')
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState(null)

  const readDevice = async () => {
    if (!pushSupported()) {
      setState(iosNeedsInstall() ? 'ios-needs-install' : 'unsupported')
      return
    }
    const subscription = await currentSubscription()
    setEndpoint(subscription?.endpoint ?? null)
    setState(subscription ? 'on' : permission() === 'denied' ? 'denied' : 'off')
  }

  useEffect(() => { readDevice() }, [])

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['push-devices'] })
    readDevice()
  }

  const thisDevice = devices?.find((d) => d.endpoint === endpoint) ?? null

  const turnOn = async () => {
    setBusy(true); setNote(null)
    try {
      const result = await enablePush(config?.publicKey)
      if (!result.ok) {
        setNote(
          result.reason === 'denied'
            ? 'Your browser is blocking notifications for this site. Allow them in the padlock menu beside the address bar, then try again.'
            : result.reason === 'not-configured'
              ? 'Notifications are not set up on this server yet.'
              : 'No permission was given, so this device will not be notified.',
        )
      }
    } catch {
      setNote('Could not turn notifications on for this device. Please try again.')
    } finally {
      setBusy(false)
      refresh()
    }
  }

  const turnOff = async () => {
    setBusy(true); setNote(null)
    try { await disablePush() } finally { setBusy(false); refresh() }
  }

  const test = useMutation({
    mutationFn: (id) => api.post(`/notifications/devices/${id}/test`),
    onSuccess: () => setNote('Sent — it should appear in a moment.'),
    onError: (err) => setNote(err.response?.data?.message ?? 'Could not send a test notification.'),
  })

  const setUrgentOnly = useMutation({
    mutationFn: (urgentOnly) => enablePush(config?.publicKey, { urgentOnly }),
    onSuccess: refresh,
  })

  const remove = useMutation({
    mutationFn: (id) => api.delete(`/notifications/devices/${id}`),
    onSuccess: refresh,
  })

  if (isPending || error) return <CardState title="Notifications" isPending={isPending} error={error} />

  return (
    <div className="space-y-4">
      <motion.section
        initial={{ opacity: 0, y: 8 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.28, ease: 'easeOut' }}
        className="theme-fade rounded-card border border-line bg-card p-4 shadow-sm sm:p-5"
      >
        <h2 className="text-md font-bold">Notifications</h2>
        <p className="mt-0.5 text-xs text-muted">
          Be told the moment your AI books someone in, even with this app closed. Emergencies and
          anything happening today keep notifying until somebody marks them as seen.
        </p>

        {/* The server has no key pair, so nothing can be delivered to anyone. */}
        {!config?.enabled && (
          <p className="mt-4 rounded-2xl bg-panel px-4 py-3 text-sm text-ink-soft">
            Notifications are not switched on for this server yet. Whoever runs it needs to
            generate a key pair once — it is free and takes a minute. Until then, anything that
            needs you still waits on your Dashboard.
          </p>
        )}

        {config?.enabled && (
          <div className="mt-4 space-y-3">
            {state === 'ios-needs-install' && (
              <div className="rounded-2xl bg-panel px-4 py-3 text-sm text-ink-soft">
                <p className="font-semibold text-ink">Add Frontly to your Home Screen first</p>
                <p className="mt-1">
                  On iPhone and iPad, notifications only work once the app is installed. Tap the
                  Share button, then <span className="font-semibold">Add to Home Screen</span>, and
                  open Frontly from there — this page will then offer to turn them on.
                </p>
              </div>
            )}

            {state === 'unsupported' && (
              <p className="rounded-2xl bg-panel px-4 py-3 text-sm text-ink-soft">
                This browser cannot show notifications. Try Chrome, Edge or Firefox — or use your
                phone, which is where these are most useful anyway.
              </p>
            )}

            {(state === 'off' || state === 'denied' || state === 'on') && (
              <div className="flex flex-col gap-3 rounded-2xl bg-panel px-4 py-3 sm:flex-row sm:items-center">
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-bold">
                    {state === 'on' ? 'This device is being notified' : 'This device is not being notified'}
                  </p>
                  <p className="mt-0.5 text-xs text-muted">
                    {state === 'on'
                      ? 'You will hear about new bookings here.'
                      : 'Turn this on wherever you will actually see it — usually your phone.'}
                  </p>
                </div>

                {state === 'on' ? (
                  <div className="grid gap-2 sm:flex sm:shrink-0 sm:flex-wrap">
                    <PillButton variant="outline" disabled={test.isPending || !thisDevice}
                      onClick={() => thisDevice && test.mutate(thisDevice.id)}>
                      {test.isPending ? 'Sending…' : 'Send a test'}
                    </PillButton>
                    <PillButton variant="light" disabled={busy} onClick={turnOff}>
                      {busy ? 'Working…' : 'Turn off'}
                    </PillButton>
                  </div>
                ) : (
                  <PillButton className="w-full sm:w-auto sm:shrink-0" disabled={busy} onClick={turnOn}>
                    {busy ? 'Working…' : 'Turn on for this device'}
                  </PillButton>
                )}
              </div>
            )}

            {state === 'on' && thisDevice && (
              <label className="flex items-start gap-2.5 px-1 text-sm text-ink-soft">
                <input
                  type="checkbox"
                  className="mt-0.5 h-4 w-4 rounded accent-ink"
                  checked={thisDevice.urgentOnly}
                  disabled={setUrgentOnly.isPending}
                  onChange={(e) => setUrgentOnly.mutate(e.target.checked)}
                />
                <span>
                  Only urgent things on this device
                  <span className="block text-xs text-muted">
                    Emergencies and anything booked for today. Quieter, but you will not hear about
                    next week&rsquo;s bookings until you open the app.
                  </span>
                </span>
              </label>
            )}

            {note && (
              <p className="rounded-2xl bg-panel px-4 py-2.5 text-sm font-medium text-ink-soft">{note}</p>
            )}
          </div>
        )}
      </motion.section>

      {devices?.length > 0 && (
        <motion.section
          initial={{ opacity: 0, y: 8 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration: 0.28, ease: 'easeOut' }}
          className="theme-fade rounded-card border border-line bg-card p-4 shadow-sm sm:p-5"
        >
          <h2 className="text-md font-bold">Devices being notified</h2>
          <p className="mt-0.5 text-xs text-muted">
            Everyone on your team who has turned notifications on. Remove a phone you no longer carry.
          </p>

          <div className="mt-4 space-y-2">
            {devices.map((device) => (
              <div key={device.id} className="flex flex-wrap items-center gap-x-3 gap-y-1.5 rounded-2xl bg-panel px-4 py-2.5">
                <span className="w-full min-w-0 truncate text-sm font-semibold sm:w-auto sm:flex-1">
                  {device.label ?? 'Unknown device'}
                </span>
                {device.endpoint === endpoint && <Chip tone="mint">This device</Chip>}
                {device.urgentOnly && <Chip tone="cream">Urgent only</Chip>}
                <button
                  onClick={() => remove.mutate(device.id)}
                  disabled={remove.isPending}
                  className="rounded-pill px-3 py-1 text-xs font-semibold text-danger transition hover:bg-danger-soft disabled:opacity-50"
                >
                  Remove
                </button>
              </div>
            ))}
          </div>
        </motion.section>
      )}
    </div>
  )
}

/* ------------------------------------------------------------------ panels */

function ProfilePanel() {
  const user = useAuthStore((s) => s.user)
  const { data: org } = useQuery({
    queryKey: ['organization'],
    queryFn: () => api.get('/settings/organization').then(unwrap),
  })

  const name = user?.fullName ?? 'Unknown user'

  return (
    <div className="space-y-4">
      <motion.section
        initial={{ opacity: 0, y: 8 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.28, ease: 'easeOut' }}
        className="theme-fade rounded-card border border-line bg-card p-4 shadow-sm sm:p-5"
      >
        {/*
          The role chip goes under the name on a phone rather than beside it. Sharing the row with
          an 80px avatar left the text about 190px on a small screen, which truncated a person's
          own name to "Vict…" — the one thing on this card that should always be readable.
        */}
        <div className="flex items-center gap-4">
          <Avatar name={name} size="xl" tone="lavender" />
          <div className="min-w-0 flex-1">
            <p className="truncate text-lg font-bold">{name}</p>
            <p className="truncate text-sm text-ink-soft">{user?.email}</p>
            <p className="truncate text-xs text-muted">{org?.name ?? '—'}</p>
            <div className="mt-2 sm:hidden">
              <Chip tone="lavender">{user?.role ?? 'Member'}</Chip>
            </div>
          </div>
          <div className="hidden shrink-0 sm:block">
            <Chip tone="lavender">{user?.role ?? 'Member'}</Chip>
          </div>
        </div>
      </motion.section>

      <EditableCard
        title="Personal Information"
        subtitle="Your sign-in identity. Ask an administrator to change these."
      >
        <Field label="Full Name">
          <TextInput locked value={name} readOnly />
        </Field>
        <Field label="Role">
          <TextInput locked value={user?.role ?? '—'} readOnly />
        </Field>
        <Field label="Email Address">
          <TextInput locked value={user?.email ?? '—'} readOnly />
        </Field>
        <Field label="Organization">
          <TextInput locked value={org?.name ?? '—'} readOnly />
        </Field>
      </EditableCard>
    </div>
  )
}

function BusinessPanel() {
  const qc = useQueryClient()
  const { data: org, isPending, error } = useQuery({
    queryKey: ['organization'],
    queryFn: () => api.get('/settings/organization').then(unwrap),
  })

  const [form, setForm] = useState(null)
  const [hours, setHours] = useState(null)
  const [editing, setEditing] = useState(false)
  const [editingHours, setEditingHours] = useState(false)

  useEffect(() => {
    if (!org) return
    setForm(org)
    setHours(parseBusinessHours(org.businessHoursJson))
  }, [org])

  const save = useMutation({
    mutationFn: (body) => api.put('/settings/organization', body).then(unwrap),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['organization'] })
      // The AI prompt quotes the hours, so its preview must not keep showing the old ones.
      qc.invalidateQueries({ queryKey: ['final-prompt'] })
      setEditing(false)
      setEditingHours(false)
    },
  })

  if (isPending || error || !form || !hours)
    return <CardState title="Business information" isPending={isPending} error={error} />

  const set = (k) => (e) => setForm({ ...form, [k]: e.target.value })

  // The API replaces every column, so both cards post the full object including whatever is
  // currently on screen in the other one — otherwise saving details would revert edited hours.
  const payload = () => ({ ...form, businessHoursJson: serializeBusinessHours(hours) })

  const cancel = () => { setForm(org); save.reset(); setEditing(false) }
  const cancelHours = () => {
    setHours(parseBusinessHours(org.businessHoursJson))
    save.reset()
    setEditingHours(false)
  }

  const badHourRows = invalidHourRows(hours)
  const hoursError = badHourRows.length > 0
    ? `Closing time must be after opening time on ${badHourRows.map((r) => r.label).join(', ')}.`
    : errorText(save, 'Could not save. Please retry.')

  return (
    <div className="space-y-4">
      <EditableCard
        title="Business Information"
        subtitle="Shown to callers and used in the AI prompt"
        editing={editing}
        onEdit={() => setEditing(true)}
        onCancel={cancel}
        onSave={() => save.mutate(payload())}
        saving={save.isPending}
        error={errorText(save, 'Could not save. Please retry.')}
      >
        <Field label="Business Name">
          <TextInput locked={!editing} value={form.name ?? ''} onChange={set('name')} />
        </Field>
        <Field label="Industry">
          <TextInput locked={!editing} value={form.industry ?? ''} onChange={set('industry')} />
        </Field>
        <Field label="Phone Number">
          <TextInput locked={!editing} value={form.phone ?? ''} onChange={set('phone')} />
        </Field>
        <Field label="Email Address">
          <TextInput locked={!editing} value={form.email ?? ''} onChange={set('email')} />
        </Field>
        <Field label="Address" full>
          <TextInput locked={!editing} value={form.address ?? ''} onChange={set('address')} />
        </Field>
        <Field label="Currency">
          <TextInput locked={!editing} value={form.currency ?? ''} onChange={set('currency')} />
        </Field>
        <Field label="Timezone (IANA id)" hint="e.g. America/New_York, Asia/Dhaka">
          <TextInput locked={!editing} value={form.timezone ?? ''} onChange={set('timezone')} />
        </Field>
      </EditableCard>

      <EditableCard
        title="Business Hours"
        subtitle={`When you take appointments — local time in ${form.timezone || 'UTC'}`}
        editing={editingHours}
        onEdit={() => setEditingHours(true)}
        onCancel={cancelHours}
        onSave={() => { if (badHourRows.length === 0) save.mutate(payload()) }}
        saving={save.isPending}
        error={editingHours ? hoursError : null}
      >
        <div className="space-y-2 sm:col-span-2">
          {hours.map((row, i) => (
            <HoursRow
              key={row.key}
              row={row}
              editing={editingHours}
              onChange={(next) => setHours(hours.map((r, j) => (j === i ? next : r)))}
            />
          ))}
          <p className="px-1 pt-1 text-xs text-muted">
            The AI offers slots only inside these hours and refuses to book outside them. Someone
            who works part of the week gets their own hours under Team.
          </p>
        </div>
      </EditableCard>

      <HolidaysCard />
    </div>
  )
}

// Retell platform voices. Mirrors backend/Shared/RetellVoices.cs — change both together.
// The set is closed on purpose: expressive mode is on for every account and Retell only
// honours it for platform voices, so anything else would quietly sound flatter.
const VOICES = [
  { id: 'retell-Grace', name: 'Grace', gender: 'female' },
  { id: 'retell-Ashley', name: 'Ashley', gender: 'female' },
  { id: 'retell-Chloe', name: 'Chloe', gender: 'female' },
  { id: 'retell-Nico', name: 'Nico', gender: 'male' },
]

// Same rule the server applies on sync (RetellVoices.Resolve): a voice saved under an older
// provider prefix is recognised by name, so an account still on 11labs-Grace shows Grace rather
// than an empty box. Anything with no counterpart falls back to the first voice.
const resolveVoice = (voice) => {
  const text = (voice ?? '').trim().toLowerCase()
  const match = VOICES.find(
    (v) => text === v.id.toLowerCase() || text === v.name.toLowerCase() || text.endsWith(`-${v.name.toLowerCase()}`),
  )
  return (match ?? VOICES[0]).id
}

const LANGUAGES = ['en-US', 'en-GB', 'es-ES', 'fr-FR', 'de-DE']

function AgentPanel() {
  const qc = useQueryClient()
  const { data: agent, isPending, error } = useQuery({
    queryKey: ['agent'],
    queryFn: () => api.get('/settings/agent').then(unwrap),
  })
  const { data: status } = useQuery({
    queryKey: ['retell-status'],
    queryFn: () => api.get('/retell/status').then(unwrap),
  })

  const [form, setForm] = useState(null)
  const [editing, setEditing] = useState(false)
  useEffect(() => { if (agent) setForm(agent) }, [agent])

  const save = useMutation({
    mutationFn: (body) => api.put('/settings/agent', body).then(unwrap),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['agent'] })
      setEditing(false)
    },
  })

  if (isPending || error || !form)
    return <CardState title="AI Agent" isPending={isPending} error={error} />

  const set = (k) => (e) => setForm({ ...form, [k]: e.target.value })
  const cancel = () => { setForm(agent); save.reset(); setEditing(false) }

  return (
    <div className="space-y-4">
      <motion.section
        initial={{ opacity: 0, y: 8 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.28, ease: 'easeOut' }}
        className="theme-fade rounded-card border border-line bg-card p-4 shadow-sm sm:p-5"
      >
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h2 className="text-md font-bold">AI phone agent</h2>
            <p className="mt-0.5 text-xs text-muted">
              Set up and maintained for you by the platform team
            </p>
          </div>
          <Chip tone={status?.connected ? 'mint' : 'cream'}>
            {status?.connected ? 'Answering calls' : 'Not set up yet'}
          </Chip>
        </div>

        <div className="mt-4 grid gap-3 sm:grid-cols-2">
          <div className="flex items-center justify-between gap-3 rounded-2xl bg-panel px-4 py-3">
            <span className="text-sm text-ink-soft">Retell phone number</span>
            <span className="max-w-[55%] truncate text-xs font-semibold">
              {status?.retellPhoneNumber ?? 'Not assigned'}
            </span>
          </div>
          <div className="flex items-center justify-between gap-3 rounded-2xl bg-panel px-4 py-3">
            <span className="text-sm text-ink-soft">Transfer number</span>
            <span className="max-w-[55%] truncate text-xs font-semibold">
              {agent.transferNumber ?? 'Not set'}
            </span>
          </div>
        </div>

        <p className="mt-3 text-xs text-muted">
          Your phone numbers are managed for you — contact support to change the number your AI
          answers or where calls are transferred.
        </p>
      </motion.section>

      <EditableCard
        title="Voice & Greeting"
        subtitle="How the agent sounds and what callers hear first"
        editing={editing}
        onEdit={() => setEditing(true)}
        onCancel={cancel}
        onSave={() => save.mutate(form)}
        saving={save.isPending}
        error={errorText(save, 'Could not save. Please retry.')}
      >
        <Field label="Voice" hint="Expressive delivery is on for every voice here.">
          <SelectInput locked={!editing} value={resolveVoice(form.voice)} onChange={set('voice')}>
            {VOICES.map((v) => (
              <option key={v.id} value={v.id}>{v.name} — {v.gender}</option>
            ))}
          </SelectInput>
        </Field>
        <Field label="Language">
          <SelectInput locked={!editing} value={form.language ?? 'en-US'} onChange={set('language')}>
            {[...new Set([...LANGUAGES, form.language].filter(Boolean))].map((v) => <option key={v}>{v}</option>)}
          </SelectInput>
        </Field>
        <Field
          label="Greeting"
          full
          hint="The first thing callers hear. Leave it empty to fall back to a greeting generated from your business name."
        >
          <TextArea rows={3} locked={!editing} value={form.greeting ?? ''} onChange={set('greeting')}
            placeholder="Leave blank to use the default greeting for your business" />
        </Field>
      </EditableCard>
    </div>
  )
}

const THEME_OPTIONS = [
  {
    mode: 'light',
    label: 'Light',
    hint: 'Always the daytime palette',
    icon: ['M12 17a5 5 0 1 0 0-10 5 5 0 0 0 0 10z', 'M12 1v2M12 21v2M4.2 4.2l1.4 1.4M18.4 18.4l1.4 1.4M1 12h2M21 12h2M4.2 19.8l1.4-1.4M18.4 5.6l1.4-1.4'],
  },
  {
    mode: 'dark',
    label: 'Night',
    hint: 'Always the dark palette',
    icon: ['M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z'],
  },
  {
    mode: 'system',
    label: 'System',
    hint: 'Follow your device setting',
    icon: ['M3 4h18v12H3z', 'M8 20h8M12 16v4'],
  },
]

function AppearancePanel() {
  const mode = useThemeStore((s) => s.mode)
  const setMode = useThemeStore((s) => s.setMode)

  return (
    <motion.section
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.28, ease: 'easeOut' }}
      className="theme-fade rounded-card border border-line bg-card p-4 shadow-sm sm:p-5"
    >
      <h2 className="text-md font-bold">Appearance</h2>
      <p className="mt-0.5 text-xs text-muted">
        Applies across the whole app and is remembered on this device.
      </p>

      <div role="radiogroup" aria-label="Theme" className="mt-5 grid gap-3 sm:grid-cols-3">
        {THEME_OPTIONS.map((option) => {
          const active = mode === option.mode
          return (
            <button
              key={option.mode}
              role="radio"
              aria-checked={active}
              onClick={() => setMode(option.mode)}
              className={`flex flex-col items-start gap-2 rounded-2xl border p-4 text-left transition ${
                active
                  ? 'border-ink bg-panel'
                  : 'border-line bg-card hover:bg-panel'
              }`}
            >
              <span className={`grid h-9 w-9 place-items-center rounded-full ${active ? 'bg-ink text-on-ink' : 'bg-panel text-ink-soft'}`}>
                <svg viewBox="0 0 24 24" className="h-[18px] w-[18px]" fill="none" stroke="currentColor"
                  strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
                  {option.icon.map((d, i) => <path key={i} d={d} />)}
                </svg>
              </span>
              <span className="text-base font-semibold">{option.label}</span>
              <span className="text-xs text-muted">{option.hint}</span>
            </button>
          )
        })}
      </div>
    </motion.section>
  )
}

/* ------------------------------------------------------------------ page */

const SECTIONS = [
  { id: 'profile', label: 'Profile', Panel: ProfilePanel },
  { id: 'business', label: 'Business', Panel: BusinessPanel },
  { id: 'team', label: 'Team', Panel: TeamPanel },
  { id: 'notifications', label: 'Notifications', Panel: NotificationsPanel },
  { id: 'agent', label: 'AI Agent', Panel: AgentPanel },
  { id: 'appearance', label: 'Appearance', Panel: AppearancePanel },
]

export default function Settings() {
  const [active, setActive] = useState('profile')
  const navigate = useNavigate()

  const Panel = SECTIONS.find((s) => s.id === active).Panel

  return (
    <div>
      <header className="mb-5 sm:mb-6">
        <h1 className="font-display text-xl font-semibold tracking-[-0.01em] sm:text-2xl">Settings</h1>
        <p className="mt-1 text-sm text-ink-soft">
          Manage your account information and preferences
        </p>
      </header>

      <div className="grid gap-4 lg:grid-cols-[210px_minmax(0,1fr)] lg:gap-5">
        <nav className="theme-fade h-max min-w-0 rounded-card border border-line bg-card p-2.5 shadow-sm lg:sticky lg:top-0 lg:p-3">
          {/*
            Every section visible at once. This was briefly a swipeable strip, which is the wrong
            trade for navigation: it hides two of the five behind a gesture with nothing on screen
            saying they are there, so finding Appearance means discovering the scroll first. Pills
            that wrap cost one extra line and hide nothing. From lg it is the sidebar list again.
          */}
          <ul className="flex flex-wrap gap-1.5 lg:flex-col lg:gap-1">
            {SECTIONS.map((section) => (
              <li key={section.id} className="lg:w-full">
                <button
                  onClick={() => setActive(section.id)}
                  aria-current={active === section.id ? 'page' : undefined}
                  className={`whitespace-nowrap rounded-2xl px-3 py-2 text-sm font-semibold transition lg:w-full lg:px-4 lg:py-2.5 lg:text-left lg:text-base ${
                    active === section.id
                      ? 'bg-lavender text-ink'
                      : 'text-ink-soft hover:bg-panel'
                  }`}
                >
                  {section.label}
                </button>
              </li>
            ))}
          </ul>

          <div className="mt-2 border-t border-line pt-2">
            <button
              onClick={async () => { await signOut(); navigate('/login') }}
              className="w-full whitespace-nowrap rounded-2xl px-3 py-2 text-left text-sm font-semibold text-danger transition hover:bg-danger-soft lg:px-4 lg:py-2.5 lg:text-base"
            >
              Sign out
            </button>
          </div>
        </nav>

        {/* Remounting on section change replays the card entrance animation, so switching
            sections reads as a change of content rather than a silent swap. */}
        <div key={active} className="min-w-0">
          <Panel />
        </div>
      </div>
    </div>
  )
}
