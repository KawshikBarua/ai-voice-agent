import { motion } from 'framer-motion'

export function Card({ children, className = '', ...rest }) {
  return (
    <motion.section
      initial={{ opacity: 0, y: 10 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.35, ease: 'easeOut' }}
      className={`rounded-card bg-card p-4 shadow-sm sm:p-5 ${className}`}
      {...rest}
    >
      {children}
    </motion.section>
  )
}

/** Title and its controls sit side by side while both fit; once the title is squeezed
 *  below ~10rem the controls drop to their own line instead of overlapping it. */
export function CardTitle({ title, subtitle, action }) {
  return (
    <div className="mb-4 flex flex-wrap items-start justify-between gap-x-3 gap-y-2.5">
      <div className="min-w-[10rem] flex-1">
        <h2 className="font-display text-lg font-semibold tracking-[-0.01em]">{title}</h2>
        {subtitle && <p className="mt-0.5 text-xs text-muted">{subtitle}</p>}
      </div>
      {action}
    </div>
  )
}

export function PillButton({ children, variant = 'dark', className = '', ...rest }) {
  const styles = {
    // The brand fill, for the one action a screen is really about. It uses brand-strong
    // rather than the logo hue itself: #0097b2 under white text is 3.46:1, which is fine
    // for an icon or a border but not for a button label.
    primary: 'bg-brand-strong text-on-brand hover:opacity-90 disabled:opacity-50',
    // text-on-ink, not text-white: ink inverts at night, so a literal white would disappear.
    dark: 'bg-ink text-on-ink hover:opacity-90 disabled:opacity-50',
    light: 'bg-panel text-ink hover:bg-line disabled:opacity-50',
    outline: 'border border-line bg-card text-ink hover:bg-panel disabled:opacity-50',
  }
  return (
    <button
      className={`inline-flex items-center justify-center gap-2 rounded-pill px-4 py-2 text-sm font-semibold transition ${styles[variant]} ${className}`}
      {...rest}
    >
      {children}
    </button>
  )
}

export function Chip({ children, tone = 'mint' }) {
  const tones = {
    mint: 'bg-mint text-ink',
    lavender: 'bg-lavender text-ink',
    cream: 'bg-cream text-ink',
    dark: 'bg-ink text-on-ink',
    red: 'bg-danger-soft text-danger',
  }
  return (
    <span className={`inline-flex items-center rounded-pill px-3 py-1 text-xs font-semibold ${tones[tone]}`}>
      {children}
    </span>
  )
}

/**
 * Initials avatar. Takes the first letter of the first and last word, so "John Doe" reads
 * JD and a single-word name still gets one letter rather than an empty circle.
 */
export function initialsOf(name) {
  const words = (name ?? '').trim().split(/\s+/).filter(Boolean)
  if (words.length === 0) return '?'
  const letters = words.length === 1 ? [words[0][0]] : [words[0][0], words[words.length - 1][0]]
  return letters.join('').toUpperCase()
}

export function Avatar({ name = '?', tone = 'lavender', size = 'md', className = '' }) {
  const tones = { lavender: 'bg-lavender', mint: 'bg-mint', cream: 'bg-cream', ink: 'bg-ink text-on-ink' }
  const sizes = {
    sm: 'h-8 w-8 text-xs',
    md: 'h-10 w-10 text-sm',
    lg: 'h-16 w-16 text-xl',
    xl: 'h-20 w-20 text-2xl',
  }
  return (
    <span
      aria-hidden="true"
      className={`grid shrink-0 place-items-center rounded-full font-bold tracking-wide ${tones[tone]} ${sizes[size]} ${className}`}
    >
      {initialsOf(name)}
    </span>
  )
}

/** Segmented control — the time-range switch above a chart. */
export function Segmented({ value, onChange, options, size = 'md' }) {
  const pad = size === 'sm' ? 'px-2.5 py-1 text-xs' : 'px-3 py-1.5 text-xs'
  return (
    <div role="tablist" className="inline-flex shrink-0 gap-0.5 rounded-pill bg-panel p-0.5">
      {options.map((o) => (
        <button
          key={o.value}
          role="tab"
          aria-selected={value === o.value}
          onClick={() => onChange(o.value)}
          className={`rounded-pill font-semibold transition ${pad} ${
            value === o.value ? 'bg-card text-ink shadow-sm' : 'text-muted hover:text-ink-soft'
          }`}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}

/**
 * Signed change against a named period. `goodWhenUp` flips the colouring for measures
 * where a rise is bad (missed calls), and the arrow repeats the sign so direction is
 * never carried by colour alone.
 */
export function Delta({ value, since, goodWhenUp = true }) {
  if (value == null) {
    return <span className="text-xs text-muted">{since ? `No ${since} to compare` : 'No comparison'}</span>
  }
  const flat = Math.abs(value) < 0.05
  const up = value > 0
  const good = flat ? null : up === goodWhenUp
  const tone = good == null ? 'text-muted' : good ? 'text-[var(--color-good)]' : 'text-[var(--color-crit)]'
  const text = `${up ? '+' : ''}${value.toFixed(Math.abs(value) >= 10 ? 0 : 1)}%`
  return (
    <span className={`inline-flex items-center gap-1 text-xs font-semibold ${tone}`}>
      <span aria-hidden="true">{flat ? '→' : up ? '↑' : '↓'}</span>
      <span className="tabular-nums">{flat ? '0%' : text}</span>
      {since && <span className="font-medium text-muted">{since}</span>}
    </span>
  )
}

export function EmptyState({ message }) {
  return (
    <div className="grid place-items-center rounded-2xl bg-panel py-10 text-center">
      <p className="text-sm text-muted">{message}</p>
    </div>
  )
}

export function statusTone(status) {
  switch (status) {
    case 'Confirmed':
    case 'Completed':
    case 'Paid':
      return 'mint'
    case 'Scheduled':
    case 'Unpaid':
      return 'cream'
    case 'Cancelled':
    case 'Missed':
      return 'red'
    default:
      return 'lavender'
  }
}
