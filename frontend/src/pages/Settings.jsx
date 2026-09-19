import { Children, useEffect, useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { motion } from 'framer-motion'
import { useNavigate } from 'react-router-dom'
import { api, signOut, unwrap } from '../api/client'
import { useAuthStore } from '../store/auth'
import { useThemeStore } from '../store/theme'
import { Avatar, Chip, PillButton } from '../components/ui'
import { icons } from '../components/AppLayout'
import {
  currentSubscription, disablePush, enablePush, iosNeedsInstall, permission, pushSupported,
} from '../lib/push'

/* ------------------------------------------------------------------ primitives */

// min-w-0 is load-bearing, not tidiness: inputs (dates and times especially) have an intrinsic
// width, and as grid/flex children their default min-width:auto lets that intrinsic width push
// the page wider than the screen instead of the field shrinking to fit.
const inputBase =
  'w-full min-w-0 rounded-2xl border px-4 py-2.5 text-base outline-none transition-colors'

/** The editable state of a control: a real box, because it is about to be typed in. */
const inputLive = `${inputBase} border-line bg-card text-ink focus:border-ink`

/**
 * One micro-caption above a control, shared by `Field` and by the handful of forms that build
 * their own rows (closures, locations, adding a person). They used to carry their own label
 * styling, so half the page introduced its fields one way and half another.
 */
const fieldLabel =
  'mb-1 block text-[0.7rem] font-semibold uppercase tracking-[0.07em] text-muted'

/**
 * One labelled control.
 *
 * The label is a quiet micro-caption rather than a bold line: on a page that is almost entirely
 * labels, a label set at the same weight as the value it introduces competes with it, and forty
 * of them turn a settings screen into a wall of headings.
 */
function Field({ label, children, full = false, hint }) {
  return (
    <div className={`min-w-0 ${full ? 'sm:col-span-2' : ''}`}>
      <label className={fieldLabel}>{label}</label>
      {children}
      {hint && <p className="mt-1.5 text-xs leading-relaxed text-muted">{hint}</p>}
    </div>
  )
}

/**
 * A field that is not being edited.
 *
 * Locked controls used to render as disabled inputs — a grey box with a value sitting in it. A
 * settings page spends most of its life in that state, so the whole screen read as a form that
 * had been switched off, which is both wrong (most of it is simply information) and the single
 * loudest reason it looked unfinished. A value is now typeset as a value, over a hairline: it is
 * legible at full contrast, it still occupies an input's height so nothing jumps when Edit is
 * pressed, and the difference between "reading" and "editing" is unmistakable.
 */
function ReadValue({ value, multiline = false }) {
  const empty = value === null || value === undefined || value === ''
  return (
    <p
      className={`min-h-[2.75rem] w-full min-w-0 border-b border-line px-0.5 py-2.5 text-base ${
        empty ? 'text-muted' : 'text-ink'
      } ${multiline ? 'whitespace-pre-wrap leading-relaxed' : 'truncate'}`}
    >
      {empty ? '—' : value}
    </p>
  )
}

function TextInput({ locked, className = '', ...rest }) {
  if (locked) return <ReadValue value={rest.value} />
  return <input className={`${inputLive} ${className}`} {...rest} />
}

/**
 * Locked selects show the chosen option's *label*, not its value — the voice picker stores an
 * identifier, and printing `11labs-Grace` where the page had been showing "Grace — female" is
 * the kind of detail that makes software feel unfinished.
 */
function SelectInput({ locked, children, ...rest }) {
  if (locked) {
    const chosen = Children.toArray(children).find(
      (option) => String(option.props?.value ?? option.props?.children) === String(rest.value),
    )
    const label = chosen
      ? Children.toArray(chosen.props.children).map((part) => (typeof part === 'object' ? part.props?.children : part)).join('')
      : rest.value
    return <ReadValue value={label} />
  }

  return (
    <select className={inputLive} {...rest}>
      {children}
    </select>
  )
}

function TextArea({ locked, className = '', ...rest }) {
  if (locked) return <ReadValue value={rest.value} multiline />
  return <textarea className={`${inputLive} ${className}`} {...rest} />
}

const PencilIcon = () => (
  <svg viewBox="0 0 24 24" className="h-3.5 w-3.5" fill="none" stroke="currentColor"
    strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
    <path d="M12 20h9" />
    <path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4z" />
  </svg>
)

/**
 * Every card on this page.
 *
 * There were nine copies of this chrome, each with its own heading size and padding, which is
 * what made the screen look assembled rather than designed. One component means one type scale,
 * one radius, one shadow and one rule under every heading — and a new section cannot drift from
 * the others without someone deliberately making it.
 *
 * The header rule is the load-bearing part: cards on this page carry between one and twenty
 * controls, and without a line the eye cannot tell a card's title from a field label below it.
 */
function SettingsCard({ title, subtitle, action, children, className = '', bodyClassName = '' }) {
  return (
    <motion.section
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.28, ease: 'easeOut' }}
      className={`theme-fade overflow-hidden rounded-card border border-line bg-card shadow-sm ${className}`}
    >
      {(title || action) && (
        <header className="flex flex-wrap items-start justify-between gap-x-3 gap-y-2 border-b border-line px-5 py-4 sm:px-6">
          <div className="min-w-[12rem] flex-1">
            <h2 className="font-display text-base font-semibold tracking-[-0.01em] text-ink">{title}</h2>
            {subtitle && <p className="mt-1 text-[0.8rem] leading-relaxed text-muted">{subtitle}</p>}
          </div>
          {action}
        </header>
      )}
      <div className={`px-5 py-5 sm:px-6 ${bodyClassName}`}>{children}</div>
    </motion.section>
  )
}

/**
 * The quiet control in a card header. Deliberately not a filled button: there are five of these
 * down the page and five filled buttons would each claim to be the thing to do next.
 */
function EditButton({ onClick, label = 'Edit' }) {
  return (
    <button
      onClick={onClick}
      className="inline-flex shrink-0 items-center gap-1.5 rounded-pill px-3 py-1.5 text-xs font-semibold text-ink-soft transition hover:bg-panel hover:text-ink"
    >
      <PencilIcon />
      {label}
    </button>
  )
}

/**
 * A settings card that is read-only until Edit is pressed. Locking by default is what stops
 * a stray keystroke on a page full of inputs from silently changing the business the AI
 * quotes prices from.
 */
function EditableCard({ title, subtitle, editing, onEdit, onCancel, onSave, saving, error, children, footer }) {
  return (
    <SettingsCard
      title={title}
      subtitle={subtitle}
      action={onEdit && !editing ? <EditButton onClick={onEdit} /> : null}
    >
      {/* gap-x is wider than gap-y on purpose: two columns of fields need a visible channel
          between them, while rows of a form want to read as one block. */}
      <div className="grid gap-x-8 gap-y-5 sm:grid-cols-2">{children}</div>

      {error && (
        <p className="mt-5 rounded-2xl bg-danger-soft px-4 py-2.5 text-sm font-medium text-danger">
          {error}
        </p>
      )}

      {/* The actions sit below a rule and to the right, where a form's commit belongs — and
          only exist while there is something to commit. */}
      {editing && (
        <div className="mt-6 flex flex-col gap-2.5 border-t border-line pt-5 sm:flex-row sm:justify-end">
          <PillButton variant="outline" onClick={onCancel} disabled={saving}>
            Cancel
          </PillButton>
          <PillButton variant="primary" onClick={onSave} disabled={saving}>
            {saving ? 'Saving…' : 'Save changes'}
          </PillButton>
        </div>
      )}

      {footer}
    </SettingsCard>
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
    <SettingsCard title={title}>
      <p className={`text-sm ${isPending ? 'text-muted' : 'text-danger'}`}>{message}</p>
    </SettingsCard>
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
 * Reading and editing are two different layouts, because they are two different jobs. A week of
 * opening times is a *table* — seven days down the left, seven times down the right, scanned in
 * a second — and that is what it now is when nobody is editing: no disabled checkboxes, no
 * greyed time pickers, no seven stacked grey slabs each with 500px of nothing on its right.
 *
 * Editing brings the controls in, on the same grid, so the times stay in the column the reader
 * was just looking at. Below sm the row becomes two lines (day and open-state, then the times)
 * and the times take the full width, which also makes them far easier to hit.
 */
function HoursRow({ row, editing, onChange }) {
  const timeInput =
    'min-w-0 flex-1 rounded-xl border border-line bg-card px-3 py-2 text-sm text-ink outline-none transition-colors focus:border-ink sm:flex-none sm:py-1.5'

  if (!editing) {
    return (
      <div className="flex items-baseline justify-between gap-4 py-2.5">
        <span className={`text-sm font-semibold ${row.open ? 'text-ink' : 'text-muted'}`}>
          {row.label}
        </span>
        {row.open ? (
          <span className="text-sm tabular-nums text-ink-soft">
            {row.start} <span className="text-muted">–</span> {row.end}
          </span>
        ) : (
          <span className="text-sm text-muted">Closed</span>
        )}
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-2 rounded-2xl bg-panel px-3.5 py-2.5 sm:flex-row sm:flex-wrap sm:items-center sm:gap-x-4 sm:gap-y-2 sm:px-4">
      <div className="flex items-center justify-between gap-3 sm:contents">
        <span className="text-sm font-semibold sm:w-[92px] sm:shrink-0">{row.label}</span>

        <label className="flex shrink-0 items-center gap-2 text-sm text-ink-soft">
          <input
            type="checkbox"
            className="h-4 w-4 rounded accent-brand-strong"
            checked={row.open}
            onChange={(e) => onChange({ ...row, open: e.target.checked })}
          />
          Open
        </label>
      </div>

      {/* Pushed to the right edge so the times stay in the column they occupy when the card is
          being read — switching to Edit should not move the thing you came to change. */}
      {row.open ? (
        <div className="flex items-center gap-2 sm:ml-auto">
          <input type="time" className={timeInput} value={row.start}
            onChange={(e) => onChange({ ...row, start: e.target.value })} aria-label={`${row.label} opening time`} />
          <span className="shrink-0 text-sm text-muted">to</span>
          <input type="time" className={timeInput} value={row.end}
            onChange={(e) => onChange({ ...row, end: e.target.value })} aria-label={`${row.label} closing time`} />
        </div>
      ) : (
        <span className="text-sm font-medium text-muted sm:ml-auto">Closed all day</span>
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
    return <CardState title="Holidays & closures" isPending={isPending} error={error} />

  const addError = errorText(add, 'Could not add that closure. Please retry.')
  const today = todayStart()
  const field = 'min-w-0 rounded-2xl border border-line bg-card px-4 py-2.5 text-base outline-none focus:border-ink'

  return (
    <SettingsCard
      title="Holidays & closures"
      subtitle="Days the business is shut. These override your weekly hours — the AI will not book anyone in, and tells callers why."
    >
      {/* Stacked and full-width on a phone; the same single line as before from sm. */}
      <form
        className="grid gap-2.5 sm:flex sm:flex-wrap sm:items-end"
        onSubmit={(e) => { e.preventDefault(); add.mutate({ date, name: name.trim() || 'Closed' }) }}
      >
        <div>
          <label className={fieldLabel} htmlFor="closure-date">Date</label>
          <input id="closure-date" type="date" required className={`${field} w-full sm:w-auto`} value={date}
            onChange={(e) => setDate(e.target.value)} />
        </div>
        <div className="sm:min-w-[180px] sm:flex-1">
          <label className={fieldLabel} htmlFor="closure-name">Reason</label>
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
    </SettingsCard>
  )
}

/* -------------------------------------------------------------- coverage areas */

// leading-6 is load-bearing rather than decoration. A native <select> sizes itself from font
// metrics and ignores line-height altogether, while an <input> takes it from the page — so the
// country box came out 41px against the city box's 44px and the two sat visibly off each other.
// Pinning the line-height fixes the input half; LocationSelect below fixes the select half.
const locationField =
  'min-w-0 rounded-2xl border border-line bg-card px-4 py-2.5 text-base leading-6 outline-none focus:border-ink'

/**
 * A country-style dropdown that is exactly as tall as the text box beside it.
 *
 * `appearance-none` is what makes that possible: it takes the select out of the browser's own
 * sizing and puts it on the same box model as an input, so the two line up at every font size.
 * The cost is the native arrow, which is why one is drawn here — `currentColor` so it follows
 * the theme, and `pointer-events-none` so it never swallows a click meant for the control.
 */
function LocationSelect({ className = '', children, ...rest }) {
  return (
    <div className="relative">
      <select className={`${locationField} w-full appearance-none pr-10 ${className}`} {...rest}>
        {children}
      </select>
      <svg
        aria-hidden="true"
        viewBox="0 0 20 20"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.75"
        className="pointer-events-none absolute right-4 top-1/2 size-4 -translate-y-1/2 text-muted"
      >
        <path d="M6 8l4 4 4-4" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </div>
  )
}

const MIN_MILES = 1
const MAX_MILES = 200

const asKm = (miles) => Math.round(miles * 1.609344)

/** Describes one branch's reach in a single phrase, the way the AI will put it to a caller. */
const describeCoverage = (l) =>
  l.coversEntireCity ? `All of ${l.city}` : `${Math.round(l.coverageRadiusMiles)} mi (${asKm(l.coverageRadiusMiles)} km) around ${l.city}`

/** Settles a fast-changing value once typing stops, so the city search runs on a pause rather
 *  than on every keystroke — one request instead of a dozen, and no flicker between them. */
function useDebounced(value, ms = 300) {
  const [settled, setSettled] = useState(value)
  useEffect(() => {
    const timer = setTimeout(() => setSettled(value), ms)
    return () => clearTimeout(timer)
  }, [value, ms])
  return settled
}

// Matches the server's own floor, and it is a floor rather than a threshold: OpenStreetMap is a
// search engine, not an autocomplete. On a short fragment it guesses at what sounds similar —
// "Lon" in the UK comes back as Brookeborough and Falkirk, with no London — and on a genuine
// prefix it often returns nothing at all ("Manche" finds no Manchester). So the full name is what
// actually works, which is what the placeholder and the empty state both say.
const MIN_CITY_QUERY = 4

/**
 * Type-ahead over the cities of one country.
 *
 * Deliberately not a plain dropdown of every city: the United States alone has tens of thousands,
 * which is megabytes to ship and unusable to scroll. The answers come from the same OpenStreetMap
 * data the coverage check itself uses, so a city that can be picked here is one an address can
 * later be measured against — and picking one is what captures the city's boundary, which is what
 * "cover the whole city" is judged on.
 */
function CityPicker({ countryCode, value, onChange, id }) {
  const [open, setOpen] = useState(false)
  const typed = value.trim()
  const query = useDebounced(typed)
  const longEnough = query.length >= MIN_CITY_QUERY

  const { data: cities, isFetching } = useQuery({
    queryKey: ['cities', countryCode, query.toLowerCase()],
    queryFn: () => api.get('/locations/cities', { params: { countryCode, q: query } }).then(unwrap),
    enabled: Boolean(countryCode) && longEnough,
    staleTime: Infinity,
  })

  // The list stays up while a new query is in flight, so the box does not empty and re-fill on
  // every pause in typing. Only the footer changes.
  const suggestions = cities ?? []
  const settled = !isFetching && query === typed

  return (
    <div className="relative">
      <input
        id={id}
        className={`${locationField} w-full`}
        value={value}
        autoComplete="off"
        disabled={!countryCode}
        placeholder={countryCode ? 'Type the full city name…' : 'Pick a country first'}
        onChange={(e) => { onChange(e.target.value); setOpen(true) }}
        onFocus={() => setOpen(true)}
        // A blur that fires before the click lands would close the list out from under the
        // pointer, so the close waits a frame for the selection to register.
        onBlur={() => setTimeout(() => setOpen(false), 120)}
      />
      {open && typed.length > 0 && (
        <ul className="absolute z-20 mt-1 max-h-56 w-full overflow-auto rounded-2xl border border-line bg-card py-1 shadow-lg">
          {suggestions.map((c) => (
            <li key={`${c.city}|${c.region}`}>
              <button
                type="button"
                className="block w-full px-4 py-2 text-left text-sm hover:bg-panel"
                // The whole suggestion is kept, not just its name: the server re-resolves it on
                // save, and this is the spelling that finds it again.
                onClick={() => { onChange(c.city); setOpen(false) }}
              >
                {c.city}
                {c.region && <span className="text-muted"> · {c.region}</span>}
              </button>
            </li>
          ))}
          {!longEnough && (
            <li className="px-4 py-2 text-sm text-muted">Keep typing the city name…</li>
          )}
          {longEnough && !settled && suggestions.length === 0 && (
            <li className="px-4 py-2 text-sm text-muted">Searching…</li>
          )}
          {/* Worth being specific: this searches real places rather than filtering a list, so a
              half-typed name usually finds nothing at all. Someone who typed "Manche" and read
              "no match" would conclude Manchester was unavailable. */}
          {longEnough && settled && suggestions.length === 0 && (
            <li className="px-4 py-2 text-sm text-muted">
              No match — try the city's full name, spelled out.
            </li>
          )}
        </ul>
      )}
    </div>
  )
}

/** The radius control, plus the switch that makes it irrelevant. */
function CoverageInput({ miles, entireCity, city, onMiles, onEntireCity, onCommit, idPrefix }) {
  return (
    <div className="space-y-2">
      <label className="flex items-center gap-2 text-sm">
        <input
          type="checkbox"
          className="size-4 accent-brand-strong"
          checked={entireCity}
          onChange={(e) => onEntireCity(e.target.checked)}
        />
        <span>Cover the whole of {city || 'the city'}</span>
      </label>

      <div className={entireCity ? 'pointer-events-none opacity-40' : ''}>
        <div className="flex items-baseline justify-between text-xs text-muted">
          <label htmlFor={`${idPrefix}-radius`}>Coverage radius</label>
          <span className="font-semibold text-ink-soft">{Math.round(miles)} mi · {asKm(miles)} km</span>
        </div>
        <input
          id={`${idPrefix}-radius`}
          type="range"
          min={MIN_MILES}
          max={MAX_MILES}
          step={1}
          value={miles}
          disabled={entireCity}
          className="mt-1 w-full accent-brand-strong"
          onChange={(e) => onMiles(Number(e.target.value))}
          // Saved when the thumb is let go rather than on every pixel of the drag: a slider
          // fires a change per step, and a hundred of those would be a hundred requests.
          onPointerUp={onCommit}
          onKeyUp={onCommit}
          onBlur={onCommit}
        />
      </div>
    </div>
  )
}

/** One saved branch. Coverage is edited in place — the slider writes when it is let go. */
function LocationRow({ location, onSave, onRemove, busy }) {
  const [draft, setDraft] = useState(location)
  useEffect(() => setDraft(location), [location])

  const changed = draft.coverageRadiusMiles !== location.coverageRadiusMiles ||
    draft.coversEntireCity !== location.coversEntireCity

  return (
    <div className="rounded-2xl bg-panel px-4 py-3">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <span className="min-w-0 flex-1 truncate text-sm font-semibold">{location.name}</span>
        <Chip tone="cream">{location.city}, {location.countryName}</Chip>
        <button
          onClick={() => onRemove(location.id)}
          disabled={busy}
          className="rounded-pill px-3 py-1 text-xs font-semibold text-danger transition hover:bg-danger-soft disabled:opacity-50"
        >
          Remove
        </button>
      </div>

      <div className="mt-3 max-w-sm">
        <CoverageInput
          idPrefix={`loc-${location.id}`}
          city={location.city}
          miles={draft.coverageRadiusMiles}
          entireCity={draft.coversEntireCity}
          onMiles={(coverageRadiusMiles) => setDraft({ ...draft, coverageRadiusMiles })}
          onEntireCity={(coversEntireCity) => {
            setDraft({ ...draft, coversEntireCity })
            onSave({ ...draft, coversEntireCity })
          }}
          onCommit={() => { if (changed) onSave(draft) }}
        />
      </div>
    </div>
  )
}

const blankLocation = { countryCode: '', city: '', name: '', coverageRadiusMiles: 10, coversEntireCity: false }

/**
 * The branches the business works out of, and how far each one travels.
 *
 * This is what an address given on a call is checked against: the AI looks it up on
 * OpenStreetMap the moment the caller says it, and either books it, books it with a warning that
 * someone will ring back, or explains that it is outside the area — rather than everyone finding
 * out on the day. With no branches here nothing is checked and every address is accepted, which
 * is how every business starts.
 */
function LocationsCard() {
  const qc = useQueryClient()
  const { data: locations, isPending, error } = useQuery({
    queryKey: ['locations'],
    queryFn: () => api.get('/locations').then(unwrap),
  })
  // Fixed reference data — fetched once and kept for the session.
  const { data: countries } = useQuery({
    queryKey: ['countries'],
    queryFn: () => api.get('/locations/countries').then(unwrap),
    staleTime: Infinity,
  })

  const [form, setForm] = useState(blankLocation)

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['locations'] })
    // The prompt names the areas covered, so its preview goes stale otherwise.
    qc.invalidateQueries({ queryKey: ['final-prompt'] })
  }

  const add = useMutation({
    mutationFn: (body) => api.post('/locations', body).then(unwrap),
    onSuccess: () => { refresh(); setForm(blankLocation) },
  })
  const save = useMutation({
    mutationFn: (body) => api.put(`/locations/${body.id}`, body).then(unwrap),
    onSuccess: refresh,
  })
  const remove = useMutation({
    mutationFn: (id) => api.delete(`/locations/${id}`),
    onSuccess: refresh,
  })

  if (isPending || error)
    return <CardState title="Locations & coverage" isPending={isPending} error={error} />

  const problem = errorText(add, 'Could not add that location. Please retry.') ??
    errorText(save, 'Could not save that change. Please retry.') ??
    errorText(remove, 'Could not remove that location. Please retry.')

  const ready = form.countryCode && form.city.trim().length > 1

  return (
    <SettingsCard
      title="Locations & coverage"
      subtitle="Where you work from, and how far you travel. The AI checks every address a caller gives against these before it books — an address it cannot find, or one outside every area, is caught on the phone. Leave this empty and no address is ever checked."
    >
      <form
        className="grid gap-3 sm:grid-cols-2"
        onSubmit={(e) => { e.preventDefault(); if (ready) add.mutate({ ...form, isActive: true }) }}
      >
        <div>
          <label className={fieldLabel} htmlFor="loc-country">Country</label>
          <LocationSelect
            id="loc-country"
            value={form.countryCode}
            onChange={(e) => setForm({ ...form, countryCode: e.target.value, city: '' })}
          >
            <option value="">Choose a country…</option>
            {(countries ?? []).map((c) => <option key={c.code} value={c.code}>{c.name}</option>)}
          </LocationSelect>
        </div>

        <div>
          <label className={fieldLabel} htmlFor="loc-city">City</label>
          <CityPicker
            id="loc-city"
            countryCode={form.countryCode}
            value={form.city}
            onChange={(city) => setForm({ ...form, city })}
          />
        </div>

        <div>
          <label className={fieldLabel} htmlFor="loc-name">
            Branch name <span className="font-normal text-muted">(optional)</span>
          </label>
          <input
            id="loc-name"
            className={`${locationField} w-full`}
            value={form.name}
            placeholder={form.city || 'e.g. North depot'}
            onChange={(e) => setForm({ ...form, name: e.target.value })}
          />
        </div>

        <CoverageInput
          idPrefix="loc-new"
          city={form.city}
          miles={form.coverageRadiusMiles}
          entireCity={form.coversEntireCity}
          onMiles={(coverageRadiusMiles) => setForm({ ...form, coverageRadiusMiles })}
          onEntireCity={(coversEntireCity) => setForm({ ...form, coversEntireCity })}
          onCommit={() => {}}
        />

        <div className="sm:col-span-2">
          <PillButton type="submit" className="w-full sm:w-auto" disabled={!ready || add.isPending}>
            {add.isPending ? 'Adding…' : 'Add location'}
          </PillButton>
        </div>
      </form>

      {problem && (
        <p className="mt-4 rounded-2xl bg-danger-soft px-4 py-2.5 text-sm font-medium text-danger">
          {problem}
        </p>
      )}

      <div className="mt-5 space-y-2">
        {locations.length === 0 && (
          <p className="rounded-2xl bg-panel px-4 py-3 text-sm text-muted">
            No locations yet — the AI accepts any address a caller gives.
          </p>
        )}
        {locations.map((l) => (
          <LocationRow
            key={l.id}
            location={l}
            busy={remove.isPending || save.isPending}
            onSave={(next) => save.mutate(next)}
            onRemove={(id) => remove.mutate(id)}
          />
        ))}
      </div>

      {locations.length > 0 && (
        <p className="px-1 pt-3 text-xs text-muted">
          An address inside one of these is booked as normal. One in the right city but past the
          radius is still booked, and the caller is told a colleague will ring back to confirm it.
          Anywhere else is turned down politely. Covering: {locations.map(describeCoverage).join(' · ')}.
        </p>
      )}
    </SettingsCard>
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
          <label className={fieldLabel} htmlFor={`off-from-${employee.id}`}>First day</label>
          <input id={`off-from-${employee.id}`} type="date" required className={`${field} w-full`} value={from}
            onChange={(e) => setFrom(e.target.value)} />
        </div>
        <div className="min-w-0">
          <label className={fieldLabel} htmlFor={`off-to-${employee.id}`}>Last day</label>
          <input id={`off-to-${employee.id}`} type="date" className={`${field} w-full`} value={to} min={from}
            onChange={(e) => setTo(e.target.value)} />
        </div>
        </div>
        <div className="sm:min-w-[150px] sm:flex-1">
          <label className={fieldLabel} htmlFor={`off-why-${employee.id}`}>Reason</label>
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
      <SettingsCard
        title="Team"
        subtitle="Who takes appointments. The AI can book one caller with each person at the same time — two people means two callers can both have noon."
      >
        <form
          className="grid gap-2.5 sm:flex sm:flex-wrap sm:items-end"
          onSubmit={(e) => {
            e.preventDefault()
            add.mutate({ name: newName.trim(), jobTitle: newTitle.trim() || null, isActive: true })
          }}
        >
          <div className="sm:min-w-[160px] sm:flex-1">
            <label className={fieldLabel} htmlFor="new-employee">Name</label>
            <input id="new-employee" required className={`${field} w-full`} value={newName} placeholder="e.g. James"
              onChange={(e) => setNewName(e.target.value)} />
          </div>
          <div className="sm:min-w-[160px] sm:flex-1">
            <label className={fieldLabel} htmlFor="new-employee-title">Job title</label>
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
      </SettingsCard>
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
      <SettingsCard
        title="Notifications"
        subtitle="Be told the moment your AI books someone in, even with this app closed. Emergencies and anything happening today keep notifying until somebody marks them as seen."
      >
        {/* The server has no key pair, so nothing can be delivered to anyone. */}
        {!config?.enabled && (
          <p className="rounded-2xl bg-panel px-4 py-3 text-sm text-ink-soft">
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
      </SettingsCard>

      {devices?.length > 0 && (
        <SettingsCard
          title="Devices being notified"
          subtitle="Everyone on your team who has turned notifications on. Remove a phone you no longer carry."
        >
          <div className="space-y-2">
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
        </SettingsCard>
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
      {/*
        The identity card. No heading of its own — a card whose entire content is a person's name
        does not need a title saying so, and the old "Profile" heading above a name at the same
        weight read as a label attached to nothing.

        The role chip goes under the name on a phone rather than beside it. Sharing the row with
        an 80px avatar left the text about 190px on a small screen, which truncated a person's
        own name to "Vict…" — the one thing on this card that should always be readable.
      */}
      <SettingsCard bodyClassName="!py-6">
        <div className="flex items-center gap-5">
          <Avatar name={name} size="xl" tone="lavender" />
          <div className="min-w-0 flex-1">
            <p className="truncate font-display text-xl font-semibold tracking-[-0.01em]">{name}</p>
            <p className="mt-0.5 truncate text-sm text-ink-soft">{user?.email}</p>
            <p className="mt-2 flex items-center gap-2 text-xs text-muted">
              <span className="truncate">{org?.name ?? '—'}</span>
            </p>
            <div className="mt-3 sm:hidden">
              <Chip tone="lavender">{user?.role ?? 'Member'}</Chip>
            </div>
          </div>
          <div className="hidden shrink-0 sm:block">
            <Chip tone="lavender">{user?.role ?? 'Member'}</Chip>
          </div>
        </div>
      </SettingsCard>

      <EditableCard
        title="Personal information"
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
        title="Business information"
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
        title="Business hours"
        subtitle={`When you take appointments — local time in ${form.timezone || 'UTC'}`}
        editing={editingHours}
        onEdit={() => setEditingHours(true)}
        onCancel={cancelHours}
        onSave={() => { if (badHourRows.length === 0) save.mutate(payload()) }}
        saving={save.isPending}
        error={editingHours ? hoursError : null}
      >
        {/* Hairlines between rows while reading — the week is a table, and a table wants rules,
            not seven separate slabs. The editor keeps its own spacing, because there each row
            is a group of controls rather than a line of text. */}
        <div className={`sm:col-span-2 ${editingHours ? 'space-y-2' : 'divide-y divide-line'}`}>
          {hours.map((row, i) => (
            <HoursRow
              key={row.key}
              row={row}
              editing={editingHours}
              onChange={(next) => setHours(hours.map((r, j) => (j === i ? next : r)))}
            />
          ))}
          <p className="pt-4 text-xs leading-relaxed text-muted">
            The AI offers slots only inside these hours and refuses to book outside them. Someone
            who works part of the week gets their own hours under Team.
          </p>
        </div>
      </EditableCard>

      <LocationsCard />

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
    return <CardState title="AI agent" isPending={isPending} error={error} />

  const set = (k) => (e) => setForm({ ...form, [k]: e.target.value })
  const cancel = () => { setForm(agent); save.reset(); setEditing(false) }

  return (
    <div className="space-y-4">
      <SettingsCard
        title="AI phone agent"
        subtitle="Set up and maintained for you by the platform team"
        action={
          <Chip tone={status?.connected ? 'mint' : 'cream'}>
            {status?.connected ? 'Answering calls' : 'Not set up yet'}
          </Chip>
        }
      >
        {/* Two facts, and both are numbers somebody may need to read out loud — so they are
            typeset as values on their own line rather than squeezed against their label with
            a truncation waiting to happen. */}
        <dl className="grid gap-3 sm:grid-cols-2">
          {[
            { term: 'Retell phone number', value: status?.retellPhoneNumber ?? 'Not assigned' },
            { term: 'Transfer number', value: agent.transferNumber ?? 'Not set' },
          ].map((item) => (
            <div key={item.term} className="min-w-0 rounded-2xl bg-panel px-4 py-3">
              <dt className="text-[0.7rem] font-semibold uppercase tracking-[0.07em] text-muted">
                {item.term}
              </dt>
              <dd className="mt-1 truncate text-base font-semibold tabular-nums">{item.value}</dd>
            </div>
          ))}
        </dl>

        <p className="mt-4 text-xs leading-relaxed text-muted">
          Your phone numbers are managed for you — contact support to change the number your AI
          answers or where calls are transferred.
        </p>
      </SettingsCard>

      <EditableCard
        title="Voice & greeting"
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
    <SettingsCard
      title="Appearance"
      subtitle="Applies across the whole app and is remembered on this device."
    >
      <div role="radiogroup" aria-label="Theme" className="grid gap-3 sm:grid-cols-3">
        {THEME_OPTIONS.map((option) => {
          const active = mode === option.mode
          return (
            <button
              key={option.mode}
              role="radio"
              aria-checked={active}
              onClick={() => setMode(option.mode)}
              // The chosen theme is marked by a ring rather than a 1px border swap: a border
              // that only changes colour moves nothing, so on a card this size the selection
              // was easy to miss entirely.
              className={`flex flex-col items-start gap-2 rounded-2xl border p-4 text-left transition ${
                active
                  ? 'border-brand-strong bg-panel ring-2 ring-brand-strong/25'
                  : 'border-line bg-card hover:border-brand/40 hover:bg-panel'
              }`}
            >
              <span
                className={`grid h-9 w-9 place-items-center rounded-full transition ${
                  active ? 'bg-brand-strong text-on-brand' : 'bg-panel text-ink-soft'
                }`}
              >
                <svg viewBox="0 0 24 24" className="h-[18px] w-[18px]" fill="none" stroke="currentColor"
                  strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
                  {option.icon.map((d, i) => <path key={i} d={d} />)}
                </svg>
              </span>
              <span className="text-[0.95rem] font-semibold">{option.label}</span>
              <span className="text-xs leading-relaxed text-muted">{option.hint}</span>
            </button>
          )
        })}
      </div>
    </SettingsCard>
  )
}

/* ------------------------------------------------------------------ page */

/**
 * The sections, in the order somebody actually goes looking for them: who I am, then the
 * business, then the people, then the things that reach me, then the agent, then taste.
 *
 * Each carries an icon and a one-line description. The icons are the app sidebar's own set
 * (`icons` in AppLayout) rather than a second family invented here — two icon vocabularies
 * three inches apart is the sort of thing that reads as "unfinished" without anyone being
 * able to say why. The descriptions only show on the desktop rail, where there is room: they
 * turn six one-word labels into a menu somebody can choose from without clicking each one.
 */
const SECTIONS = [
  { id: 'profile', label: 'Profile', hint: 'Your sign-in identity', icon: icons.users, Panel: ProfilePanel },
  { id: 'business', label: 'Business', hint: 'Details, hours and coverage', icon: icons.book, Panel: BusinessPanel },
  { id: 'team', label: 'Team', hint: 'Who takes appointments', icon: icons.appointments, Panel: TeamPanel },
  { id: 'notifications', label: 'Notifications', hint: 'What reaches your phone', icon: icons.bell, Panel: NotificationsPanel },
  { id: 'agent', label: 'AI agent', hint: 'Voice, greeting and numbers', icon: icons.phone, Panel: AgentPanel },
  { id: 'appearance', label: 'Appearance', hint: 'Light, night or system', icon: icons.settings, Panel: AppearancePanel },
]

/** The rail's icon. Same 24-grid and stroke weight as the app sidebar's. */
const SectionIcon = ({ d }) => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"
    strokeLinecap="round" strokeLinejoin="round" className="h-[18px] w-[18px]" aria-hidden="true">
    {d.map((path, i) => <path key={i} d={path} />)}
  </svg>
)

export default function Settings() {
  const [active, setActive] = useState('profile')
  const navigate = useNavigate()

  const current = SECTIONS.find((s) => s.id === active)

  return (
    // A reading column, like every other page in this app. Left full-bleed, a settings form on
    // a wide monitor puts a 500px box around a first name and strands its label a screen away
    // from the value — which is most of why this page looked amateur next to the rest.
    <div className="mx-auto max-w-5xl">
      <header className="mb-5 flex flex-wrap items-end justify-between gap-x-4 gap-y-3 sm:mb-7">
        <div className="min-w-0">
          <h1 className="font-display text-xl font-semibold tracking-[-0.01em] sm:text-2xl">Settings</h1>
          <p className="mt-1 text-sm text-ink-soft">
            Manage your account, your business and how your AI answers.
          </p>
        </div>

        {/*
          Sign out belongs here, not in the section list. It was the seventh item in a menu of
          six sections — the only one that was not a section, the only one in danger red, and
          sitting directly under "Appearance" where a mis-tap costs you your session.
        */}
        {/* `ml-auto` keeps it at the right edge even when it wraps onto its own line on a
            phone, where a full-width-left button directly under the title would read as the
            page's primary action rather than as the way out. */}
        <PillButton
          variant="outline"
          onClick={async () => { await signOut(); navigate('/login') }}
          className="ml-auto shrink-0"
        >
          Sign out
        </PillButton>
      </header>

      <div className="grid gap-4 lg:grid-cols-[236px_minmax(0,1fr)] lg:gap-7">
        {/*
          The rail. No card around it any more: a bordered panel next to the app's own bordered
          sidebar read as two competing navigations, and the one that was not the app's looked
          like a copy of it. Plain rows on the page, with the active row carrying the same raised
          card and brand ink the sidebar uses for the current page — one vocabulary, used twice.

          Every section stays visible at once. This was briefly a swipeable strip, which is the
          wrong trade for navigation: it hides items behind a gesture with nothing on screen
          saying they are there. Pills that wrap cost one line and hide nothing.
        */}
        <nav aria-label="Settings sections" className="h-max min-w-0 lg:sticky lg:top-0">
          <ul className="flex flex-wrap gap-1.5 lg:flex-col lg:gap-1">
            {SECTIONS.map((section) => {
              const selected = active === section.id
              return (
                <li key={section.id} className="lg:w-full">
                  <button
                    onClick={() => setActive(section.id)}
                    aria-current={selected ? 'page' : undefined}
                    className={`flex items-center gap-2.5 whitespace-nowrap rounded-2xl px-3 py-2 text-sm font-semibold transition lg:w-full lg:items-start lg:px-3.5 lg:py-2.5 lg:text-left ${
                      selected
                        ? 'bg-card text-brand-strong shadow-sm lg:ring-1 lg:ring-line'
                        : 'text-ink-soft hover:bg-card/70 hover:text-ink'
                    }`}
                  >
                    <span className={`shrink-0 lg:mt-0.5 ${selected ? 'text-brand-strong' : 'text-muted'}`}>
                      <SectionIcon d={section.icon} />
                    </span>
                    <span className="min-w-0 lg:flex-1">
                      {section.label}
                      {/* The description is desktop-only: on a phone these are pills in a row,
                          and a second line inside each would wrap the row to four. */}
                      <span
                        className={`hidden whitespace-normal text-[0.72rem] font-medium leading-snug lg:mt-0.5 lg:block ${
                          selected ? 'text-ink-soft' : 'text-muted'
                        }`}
                      >
                        {section.hint}
                      </span>
                    </span>
                  </button>
                </li>
              )
            })}
          </ul>
        </nav>

        {/* Remounting on section change replays the card entrance animation, so switching
            sections reads as a change of content rather than a silent swap. */}
        <div key={active} className="min-w-0 space-y-4">
          <current.Panel />
        </div>
      </div>
    </div>
  )
}
