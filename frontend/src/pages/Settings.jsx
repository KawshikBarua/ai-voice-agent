import { useEffect, useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { motion } from 'framer-motion'
import { useNavigate } from 'react-router-dom'
import { api, unwrap } from '../api/client'
import { useAuthStore } from '../store/auth'
import { useThemeStore } from '../store/theme'
import { Avatar, Chip, PillButton } from '../components/ui'

/* ------------------------------------------------------------------ primitives */

const inputBase =
  'w-full rounded-2xl border px-4 py-2.5 text-[13.5px] outline-none transition-colors'

/**
 * One labelled control. Locked fields keep the same shape as editable ones so the card does
 * not reflow when Edit is pressed — only the border and background change.
 */
function Field({ label, children, full = false, hint }) {
  return (
    <div className={full ? 'sm:col-span-2' : ''}>
      <label className="mb-1.5 block text-[12px] font-semibold text-ink-soft">{label}</label>
      {children}
      {hint && <p className="mt-1.5 text-[11.5px] text-muted">{hint}</p>}
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
      className="theme-fade rounded-card border border-line bg-card p-5 shadow-sm"
    >
      <div className="mb-5 flex items-start justify-between gap-3">
        <div>
          <h2 className="text-[15px] font-bold">{title}</h2>
          {subtitle && <p className="mt-0.5 text-[12px] text-muted">{subtitle}</p>}
        </div>

        {onEdit && !editing && (
          <button
            onClick={onEdit}
            className="inline-flex shrink-0 items-center gap-1.5 rounded-xl border border-line bg-card px-3 py-1.5 text-[12.5px] font-semibold text-ink-soft transition hover:bg-panel"
          >
            Edit <PencilIcon />
          </button>
        )}
      </div>

      <div className="grid gap-4 sm:grid-cols-2">{children}</div>

      {error && (
        <p className="mt-4 rounded-2xl bg-danger-soft px-4 py-2.5 text-[12.5px] font-medium text-danger">
          {error}
        </p>
      )}

      {editing && (
        <div className="mt-5 flex items-center gap-2.5">
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
    <section className="theme-fade rounded-card border border-line bg-card p-5 shadow-sm">
      <h2 className="text-[15px] font-bold">{title}</h2>
      <p className={`mt-2 text-[13px] ${isPending ? 'text-muted' : 'text-danger'}`}>{message}</p>
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

function HoursRow({ row, editing, onChange }) {
  const timeInput = `rounded-xl border px-3 py-1.5 text-[13px] outline-none transition-colors ${
    editing ? 'border-line bg-card text-ink focus:border-ink' : 'border-transparent bg-card/60 text-ink-soft'
  }`

  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-2 rounded-2xl bg-panel px-4 py-2.5">
      <span className="w-[92px] shrink-0 text-[13px] font-semibold">{row.label}</span>

      <label className="flex shrink-0 items-center gap-2 text-[12.5px] text-ink-soft">
        <input
          type="checkbox"
          className="h-4 w-4 rounded accent-ink"
          checked={row.open}
          disabled={!editing}
          onChange={(e) => onChange({ ...row, open: e.target.checked })}
        />
        Open
      </label>

      {row.open ? (
        <div className="flex items-center gap-2">
          <input type="time" className={timeInput} value={row.start} disabled={!editing}
            onChange={(e) => onChange({ ...row, start: e.target.value })} aria-label={`${row.label} opening time`} />
          <span className="text-[12.5px] text-muted">to</span>
          <input type="time" className={timeInput} value={row.end} disabled={!editing}
            onChange={(e) => onChange({ ...row, end: e.target.value })} aria-label={`${row.label} closing time`} />
        </div>
      ) : (
        <span className="text-[12.5px] font-medium text-muted">Closed all day</span>
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
  const field = 'rounded-2xl border border-line bg-card px-4 py-2.5 text-[13.5px] outline-none focus:border-ink'

  return (
    <motion.section
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.28, ease: 'easeOut' }}
      className="theme-fade rounded-card border border-line bg-card p-5 shadow-sm"
    >
      <h2 className="text-[15px] font-bold">Holidays &amp; Closures</h2>
      <p className="mt-0.5 text-[12px] text-muted">
        Days the business is shut. These override your weekly hours — the AI will not book
        anyone in, and tells callers why.
      </p>

      <form
        className="mt-4 flex flex-wrap items-end gap-2.5"
        onSubmit={(e) => { e.preventDefault(); add.mutate({ date, name: name.trim() || 'Closed' }) }}
      >
        <div>
          <label className="mb-1.5 block text-[12px] font-semibold text-ink-soft" htmlFor="closure-date">Date</label>
          <input id="closure-date" type="date" required className={field} value={date}
            onChange={(e) => setDate(e.target.value)} />
        </div>
        <div className="min-w-[180px] flex-1">
          <label className="mb-1.5 block text-[12px] font-semibold text-ink-soft" htmlFor="closure-name">Reason</label>
          <input id="closure-name" className={`${field} w-full`} value={name} placeholder="e.g. Christmas Day"
            onChange={(e) => setName(e.target.value)} />
        </div>
        <PillButton type="submit" disabled={add.isPending || !date}>
          {add.isPending ? 'Adding…' : 'Add closure'}
        </PillButton>
      </form>

      {addError && (
        <p className="mt-4 rounded-2xl bg-danger-soft px-4 py-2.5 text-[12.5px] font-medium text-danger">
          {addError}
        </p>
      )}

      <div className="mt-5 space-y-2">
        {holidays.length === 0 && (
          <p className="rounded-2xl bg-panel px-4 py-3 text-[12.5px] text-muted">
            No closures yet. Your weekly hours apply on every date.
          </p>
        )}
        {holidays.map((h) => {
          const past = new Date(h.date) < today
          return (
            <div key={h.id}
              className={`flex flex-wrap items-center gap-3 rounded-2xl bg-panel px-4 py-2.5 ${past ? 'opacity-60' : ''}`}>
              <span className="w-[150px] shrink-0 text-[13px] font-semibold">{formatClosureDate(h.date)}</span>
              <span className="min-w-0 flex-1 truncate text-[12.5px] text-ink-soft">{h.name}</span>
              {past && <Chip tone="cream">Past</Chip>}
              <button
                onClick={() => remove.mutate(h.id)}
                disabled={remove.isPending}
                className="rounded-pill px-3 py-1 text-[11.5px] font-semibold text-danger transition hover:bg-danger-soft disabled:opacity-50"
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
        className="theme-fade rounded-card border border-line bg-card p-5 shadow-sm"
      >
        <div className="flex flex-wrap items-center gap-4">
          <Avatar name={name} size="xl" tone="lavender" />
          <div className="min-w-0 flex-1">
            <p className="truncate text-[17px] font-bold">{name}</p>
            <p className="truncate text-[13px] text-ink-soft">{user?.email}</p>
            <p className="truncate text-[12px] text-muted">{org?.name ?? '—'}</p>
          </div>
          <Chip tone="lavender">{user?.role ?? 'Member'}</Chip>
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
          <p className="px-1 pt-1 text-[11.5px] text-muted">
            The AI offers slots only inside these hours and refuses to book outside them.
          </p>
        </div>
      </EditableCard>

      <HolidaysCard />
    </div>
  )
}

const VOICES = ['11labs-Adrian', '11labs-Grace', 'openai-Nova', 'openai-Alloy', 'openai-Shimmer', 'openai-Echo']
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
        className="theme-fade rounded-card border border-line bg-card p-5 shadow-sm"
      >
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h2 className="text-[15px] font-bold">AI phone agent</h2>
            <p className="mt-0.5 text-[12px] text-muted">
              Set up and maintained for you by the platform team
            </p>
          </div>
          <Chip tone={status?.connected ? 'mint' : 'cream'}>
            {status?.connected ? 'Answering calls' : 'Not set up yet'}
          </Chip>
        </div>

        <div className="mt-4 grid gap-3 sm:grid-cols-2">
          <div className="flex items-center justify-between gap-3 rounded-2xl bg-panel px-4 py-3">
            <span className="text-[12.5px] text-ink-soft">Retell phone number</span>
            <span className="max-w-[55%] truncate text-[12px] font-semibold">
              {status?.retellPhoneNumber ?? 'Not assigned'}
            </span>
          </div>
          <div className="flex items-center justify-between gap-3 rounded-2xl bg-panel px-4 py-3">
            <span className="text-[12.5px] text-ink-soft">Transfer number</span>
            <span className="max-w-[55%] truncate text-[12px] font-semibold">
              {agent.transferNumber ?? 'Not set'}
            </span>
          </div>
        </div>

        <p className="mt-3 text-[11.5px] text-muted">
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
        <Field label="Voice (Retell voice id)">
          <SelectInput locked={!editing} value={form.voice ?? VOICES[0]} onChange={set('voice')}>
            {/* The stored voice is included even if it is not one of ours, so editing an
                unfamiliar value cannot silently reset the agent to a different voice. */}
            {[...new Set([...VOICES, form.voice].filter(Boolean))].map((v) => <option key={v}>{v}</option>)}
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
      className="theme-fade rounded-card border border-line bg-card p-5 shadow-sm"
    >
      <h2 className="text-[15px] font-bold">Appearance</h2>
      <p className="mt-0.5 text-[12px] text-muted">
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
              <span className="text-[13.5px] font-semibold">{option.label}</span>
              <span className="text-[11.5px] text-muted">{option.hint}</span>
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
  { id: 'agent', label: 'AI Agent', Panel: AgentPanel },
  { id: 'appearance', label: 'Appearance', Panel: AppearancePanel },
]

export default function Settings() {
  const [active, setActive] = useState('profile')
  const logout = useAuthStore((s) => s.logout)
  const navigate = useNavigate()

  const Panel = SECTIONS.find((s) => s.id === active).Panel

  return (
    <div>
      <header className="mb-6">
        <h1 className="text-[26px] font-bold">Settings</h1>
        <p className="mt-1 text-[13px] text-ink-soft">
          Manage your account information and preferences
        </p>
      </header>

      <div className="grid gap-5 lg:grid-cols-[210px_minmax(0,1fr)]">
        <nav className="theme-fade h-max rounded-card border border-line bg-card p-3 shadow-sm lg:sticky lg:top-0">
          <ul className="flex gap-1 overflow-x-auto lg:flex-col lg:overflow-visible">
            {SECTIONS.map((section) => (
              <li key={section.id}>
                <button
                  onClick={() => setActive(section.id)}
                  aria-current={active === section.id ? 'page' : undefined}
                  className={`w-full whitespace-nowrap rounded-2xl px-4 py-2.5 text-left text-[13.5px] font-semibold transition ${
                    active === section.id
                      ? 'bg-lavender text-ink'
                      : 'text-ink-soft hover:bg-panel'
                  }`}
                >
                  {section.label}
                </button>
              </li>
            ))}

            <li className="lg:mt-2 lg:border-t lg:border-line lg:pt-2">
              <button
                onClick={() => { logout(); navigate('/login') }}
                className="w-full whitespace-nowrap rounded-2xl px-4 py-2.5 text-left text-[13.5px] font-semibold text-danger transition hover:bg-danger-soft"
              >
                Sign out
              </button>
            </li>
          </ul>
        </nav>

        {/* Remounting on section change replays the card entrance animation, so switching
            sections reads as a change of content rather than a silent swap. */}
        <div key={active}>
          <Panel />
        </div>
      </div>
    </div>
  )
}
