/** Shared number/date formatting for the dashboard. */

const CURRENCY_SYMBOLS = { USD: '$', EUR: '€', GBP: '£', AUD: 'A$', CAD: 'C$', INR: '₹', BDT: '৳' }

export const currencySymbol = (code) => CURRENCY_SYMBOLS[code] ?? `${code ?? ''} `.trimStart()

/** 1,284 → "1,284" · 12,900 → "12.9K" · 4,200,000 → "4.2M". Keeps stat tiles from wrapping. */
export function compact(n) {
  const v = Number(n) || 0
  const abs = Math.abs(v)
  if (abs >= 1_000_000) return `${trimZero(v / 1_000_000)}M`
  if (abs >= 10_000) return `${trimZero(v / 1_000)}K`
  return v.toLocaleString(undefined, { maximumFractionDigits: 0 })
}

const trimZero = (v) => v.toFixed(1).replace(/\.0$/, '')

export const money = (n, code = 'USD') => `${currencySymbol(code)}${compact(n)}`

export const moneyExact = (n, code = 'USD') =>
  `${currencySymbol(code)}${Number(n ?? 0).toLocaleString(undefined, { maximumFractionDigits: 0 })}`

/** 95 → "1h 35m" · 42 → "42 min". Talk time reads better in hours once it passes one. */
export function minutes(n) {
  const v = Math.max(0, Math.round(Number(n) || 0))
  if (v < 60) return `${v} min`
  const h = Math.floor(v / 60)
  const m = v % 60
  return m === 0 ? `${h.toLocaleString()}h` : `${h.toLocaleString()}h ${m}m`
}

/**
 * Total talk time to the second: 265 → "4m 25s" · 7389 → "2h 03m 09s".
 * Shown beside minutes so a customer can check the minutes against their call list — minutes are
 * this figure rounded up once, never each call rounded up on its own.
 */
export function talkTime(seconds) {
  const s = Math.max(0, Math.round(Number(seconds) || 0))
  if (s < 60) return `${s}s`
  const pad = (n) => String(n).padStart(2, '0')
  if (s < 3600) return `${Math.floor(s / 60)}m ${pad(s % 60)}s`
  return `${Math.floor(s / 3600).toLocaleString()}h ${pad(Math.floor((s % 3600) / 60))}m ${pad(s % 60)}s`
}

/** Seconds → "4m 05s", for average call length. */
export function duration(seconds) {
  const s = Math.max(0, Math.round(Number(seconds) || 0))
  return s < 60 ? `${s}s` : `${Math.floor(s / 60)}m ${String(s % 60).padStart(2, '0')}s`
}

/**
 * Percentage change, or null when there is no baseline to compare against — showing
 * "+100%" against a zero previous period would overstate a single call.
 */
export function pctChange(current, previous) {
  const a = Number(current) || 0
  const b = Number(previous) || 0
  if (b === 0) return null
  return ((a - b) / Math.abs(b)) * 100
}

export const signedPct = (v) => `${v > 0 ? '+' : ''}${v.toFixed(v <= -10 || v >= 10 ? 0 : 1)}%`

export const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/* ---------------------------------------------------------------- business time */

/**
 * A moment the API sent, as a Date.
 *
 * Every moment is stored and returned in UTC, but written without a "Z" ("2026-10-16T18:30:00"),
 * and JavaScript reads a date-time with no offset as the browser's own local time. That is how a
 * 14:30 New York booking (18:30 UTC) was shown as 18:30: the UTC clock, presented as local.
 * Anything that already carries an offset is read as it is. Only for moments — a plain calendar
 * date such as a closure is not one, and has no zone to convert.
 */
export function parseUtc(value) {
  if (value == null || value === '') return null
  if (value instanceof Date) return value
  const s = String(value)
  const hasOffset = /(?:[zZ]|[+-]\d{2}:?\d{2})$/.test(s)
  const d = new Date(hasOffset || !s.includes('T') ? s : `${s}Z`)
  return Number.isNaN(d.getTime()) ? null : d
}

/** Intl options with the business's zone, dropping a zone id the browser does not know so a bad
 *  setting degrades to the browser's clock instead of throwing on every render. */
function zoned(options, timeZone) {
  if (!timeZone) return options
  try {
    new Intl.DateTimeFormat(undefined, { timeZone })
    return { ...options, timeZone }
  } catch {
    return options
  }
}

/** A moment on the business's clock — what the caller was told on the phone — whatever the zone
 *  of the browser looking at it. */
export function formatInZone(value, timeZone, options = { dateStyle: 'medium', timeStyle: 'short' }) {
  const d = parseUtc(value)
  return d ? d.toLocaleString(undefined, zoned(options, timeZone)) : '—'
}

/** The business-local calendar fields of a moment: { year, month (1–12), day, hour, minute }. */
export function zonedParts(value, timeZone) {
  const d = parseUtc(value)
  if (!d) return null
  const parts = new Intl.DateTimeFormat('en-US', zoned({
    year: 'numeric', month: 'numeric', day: 'numeric', hour: 'numeric', minute: 'numeric', second: 'numeric',
    hourCycle: 'h23',
  }, timeZone)).formatToParts(d)
  const get = (type) => Number(parts.find((p) => p.type === type)?.value)
  return { year: get('year'), month: get('month'), day: get('day'), hour: get('hour') % 24, minute: get('minute'), second: get('second') }
}

/**
 * The UTC moment at which the business's clock reads `date` ("2026-10-16") at `time` ("14:30").
 * A booking typed into the dashboard is a time at the business, not wherever the person typing
 * happens to be. The zone's offset is read back from Intl twice, so a DST change between the first
 * guess and the answer still lands on the right hour.
 */
export function zonedToUtc(date, time, timeZone) {
  const [y, m, d] = String(date).split('-').map(Number)
  const [hh, mm] = String(time || '00:00').split(':').map(Number)
  const wall = Date.UTC(y, m - 1, d, hh || 0, mm || 0)
  const offset = (instant) => {
    const p = zonedParts(new Date(instant), timeZone)
    return Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute, p.second) - Math.floor(instant / 1000) * 1000
  }
  let utc = wall - offset(wall)
  utc = wall - offset(utc)
  return new Date(utc)
}

/** "In 9 days" / "Tomorrow" / "Today" — how a closure is worth reading. */
export function whenLabel(daysAway) {
  if (daysAway <= 0) return 'Today'
  if (daysAway === 1) return 'Tomorrow'
  if (daysAway < 7) return `In ${daysAway} days`
  if (daysAway < 14) return 'Next week'
  return `In ${Math.round(daysAway / 7)} weeks`
}
