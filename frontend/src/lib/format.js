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

/** "In 9 days" / "Tomorrow" / "Today" — how a closure is worth reading. */
export function whenLabel(daysAway) {
  if (daysAway <= 0) return 'Today'
  if (daysAway === 1) return 'Tomorrow'
  if (daysAway < 7) return `In ${daysAway} days`
  if (daysAway < 14) return 'Next week'
  return `In ${Math.round(daysAway / 7)} weeks`
}
