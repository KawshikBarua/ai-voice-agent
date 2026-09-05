import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'

/*
 * Dashboard charts.
 *
 * Plain SVG rather than a charting library: the whole set is a few hundred lines, it
 * inherits the theme tokens directly (so night mode needs no second palette), and it
 * adds nothing to the bundle.
 *
 * House style, applied to every chart here:
 *   · marks ≤ 24px thick, 4px rounded data-end, square at the baseline
 *   · a 2px gap in the card colour separates touching marks, never a stroke
 *   · gridlines are hairline and solid; axis text uses text tokens, never a series colour
 *   · every chart has a hover layer; the values it reveals are never printed on each mark
 */

/** Renders at the container's real pixel width so labels stay at their intended size
 *  instead of being scaled down by a viewBox on narrow screens. */
export function useWidth(fallback = 640) {
  const ref = useRef(null)
  const [width, setWidth] = useState(fallback)

  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    const observer = new ResizeObserver(([entry]) => {
      const w = entry.contentRect.width
      if (w > 0) setWidth(w)
    })
    observer.observe(el)
    return () => observer.disconnect()
  }, [])

  return [ref, width]
}

/** Axis ticks on round numbers — 0 / 25 / 50 / 75 rather than 0 / 23 / 46 / 69. */
export function niceScale(max, ticks = 4) {
  // An all-zero series gets a baseline and nothing else — inventing 1/2/3/4 would read
  // as a scale the data never reaches.
  if (!Number.isFinite(max) || max <= 0) return { max: 1, step: 1, values: [0] }
  const rough = max / ticks
  const mag = 10 ** Math.floor(Math.log10(rough))
  const step = [1, 2, 2.5, 5, 10].map((m) => m * mag).find((s) => s >= rough) ?? 10 * mag
  const top = Math.ceil(max / step) * step
  const values = []
  for (let v = 0; v <= top + 1e-9; v += step) values.push(Number(v.toFixed(6)))
  return { max: top, step, values }
}

/** Rounded at the data end, square at the baseline. */
function barPath(x, y, w, h, r) {
  const radius = Math.max(0, Math.min(r, w / 2, h))
  if (h <= 0) return ''
  return `M ${x} ${y + h} L ${x} ${y + radius} Q ${x} ${y} ${x + radius} ${y} L ${x + w - radius} ${y} Q ${x + w} ${y} ${x + w} ${y + radius} L ${x + w} ${y + h} Z`
}

/** Catmull-Rom → cubic, with control points clamped inside the data band so a smooth
 *  line through a zero month never dips below the baseline. */
function smoothPath(points, yMin, yMax) {
  if (points.length === 0) return ''
  if (points.length === 1) return `M ${points[0][0]} ${points[0][1]}`
  const clamp = (v) => Math.min(yMax, Math.max(yMin, v))
  let d = `M ${points[0][0]} ${points[0][1]}`
  for (let i = 0; i < points.length - 1; i++) {
    const p0 = points[Math.max(0, i - 1)]
    const p1 = points[i]
    const p2 = points[i + 1]
    const p3 = points[Math.min(points.length - 1, i + 2)]
    const c1 = [p1[0] + (p2[0] - p0[0]) / 6, clamp(p1[1] + (p2[1] - p0[1]) / 6)]
    const c2 = [p2[0] - (p3[0] - p1[0]) / 6, clamp(p2[1] - (p3[1] - p1[1]) / 6)]
    d += ` C ${c1[0]} ${c1[1]}, ${c2[0]} ${c2[1]}, ${p2[0]} ${p2[1]}`
  }
  return d
}

/** Follows the pointer, then clamps to the plot's own width so it never spills out of
 *  the card — measured rather than guessed, because the content decides how wide it is. */
function Tooltip({ x, y, width, children }) {
  const ref = useRef(null)
  const [own, setOwn] = useState(0)

  useLayoutEffect(() => {
    const w = ref.current?.offsetWidth ?? 0
    setOwn((prev) => (Math.abs(prev - w) > 1 ? w : prev))
  })

  const left = Math.max(4, Math.min(x + 12, width - own - 4))
  return (
    <div
      ref={ref}
      className="pointer-events-none absolute z-10 w-max rounded-xl bg-ink px-3 py-2 text-xs leading-snug text-on-ink shadow-lg"
      style={{ left, top: Math.max(0, y - 12), maxWidth: Math.max(140, width - 8) }}
    >
      {children}
    </div>
  )
}

export function TooltipRow({ color, label, value }) {
  return (
    <span className="mt-1 flex items-center gap-2 first:mt-0">
      {color && <span className="h-2 w-2 shrink-0 rounded-full" style={{ background: color }} />}
      <span className="flex-1 opacity-75">{label}</span>
      <span className="font-semibold tabular-nums">{value}</span>
    </span>
  )
}

/** Swatch + name + value. Always shipped for two or more series: identity never rests on
 *  the fill colour alone. */
export function Legend({ items, className = '' }) {
  return (
    <ul className={`flex flex-wrap items-center gap-x-5 gap-y-2 ${className}`}>
      {items.map((s) => (
        <li key={s.label} className="flex items-center gap-2">
          <span className="h-2.5 w-2.5 shrink-0 rounded-[3px]" style={{ background: s.color }} />
          <span className="text-xs font-medium text-ink-soft">{s.label}</span>
          {s.value != null && <span className="text-xs font-bold tabular-nums">{s.value}</span>}
        </li>
      ))}
    </ul>
  )
}

// ---------------------------------------------------------------------------
// Stacked columns — call volume split by outcome
// ---------------------------------------------------------------------------

/**
 * @param data   [{ label, sublabel?, values: [n, n, n] }]
 * @param series [{ key, label, color }] — stacked bottom-up in the order given
 */
export function StackedColumns({
  data, series, height = 240, formatValue = (v) => v.toLocaleString(),
  ariaLabel = 'Stacked column chart',
}) {
  const [ref, width] = useWidth()
  const [hover, setHover] = useState(null)

  const padLeft = 34
  const padRight = 8
  const padTop = 10
  const axisH = 22
  const plotW = Math.max(40, width - padLeft - padRight)
  const plotH = height - padTop - axisH

  const totals = data.map((d) => d.values.reduce((a, b) => a + b, 0))
  const scale = niceScale(Math.max(...totals, 0))
  const y = (v) => padTop + plotH - (v / scale.max) * plotH
  const band = plotW / Math.max(1, data.length)
  // Capped at 24px, and never wider than 60% of its band so the leftover reads as air.
  const barW = Math.min(24, Math.max(3, band * 0.6))
  const GAP = 2 // the surface gap between stacked segments

  return (
    <div ref={ref} className="relative w-full">
      <svg width={width} height={height} role="img" aria-label={ariaLabel} className="block overflow-visible">
        {scale.values.map((v) => (
          <g key={v}>
            <line x1={padLeft} x2={width - padRight} y1={y(v)} y2={y(v)}
              stroke="var(--color-grid)" strokeWidth="1" />
            <text x={padLeft - 8} y={y(v) + 4} textAnchor="end"
              className="fill-muted text-2xs tabular-nums">{formatValue(v)}</text>
          </g>
        ))}

        {data.map((d, i) => {
          const cx = padLeft + band * i + band / 2
          const x = cx - barW / 2
          const total = totals[i]
          let cursor = padTop + plotH
          const active = hover?.index === i
          return (
            <g key={d.label + i}>
              {active && (
                <rect x={cx - band / 2} y={padTop - 6} width={band} height={plotH + 6}
                  rx="8" fill="var(--color-panel)" opacity="0.7" />
              )}
              {series.map((s, si) => {
                const value = d.values[si] ?? 0
                if (value <= 0) return null
                const raw = (value / scale.max) * plotH
                const isTop = d.values.slice(si + 1).every((v) => !v)
                const h = Math.max(2, raw - (isTop ? 0 : GAP))
                const top = cursor - raw
                cursor -= raw
                return (
                  <path key={s.key} d={barPath(x, top, barW, h, isTop ? 4 : 0)}
                    fill={s.color} opacity={hover && !active ? 0.35 : 1} />
                )
              })}
              {total === 0 && (
                <line x1={x} x2={x + barW} y1={padTop + plotH} y2={padTop + plotH}
                  stroke="var(--color-axis)" strokeWidth="2" strokeLinecap="round" />
              )}
              <rect
                x={cx - band / 2} y={0} width={band} height={height - axisH}
                fill="transparent"
                onMouseEnter={() => setHover({ index: i })}
                onMouseMove={(e) => {
                  const box = e.currentTarget.ownerSVGElement.getBoundingClientRect()
                  setHover({ index: i, x: e.clientX - box.left, y: e.clientY - box.top })
                }}
                onMouseLeave={() => setHover(null)}
              />
            </g>
          )
        })}

        <line x1={padLeft} x2={width - padRight} y1={padTop + plotH} y2={padTop + plotH}
          stroke="var(--color-axis)" strokeWidth="1" />

        {data.map((d, i) => {
          // Thin the tick labels rather than let them collide.
          const every = Math.ceil((data.length * 26) / Math.max(1, plotW))
          if (i % every !== 0 && i !== data.length - 1) return null
          return (
            <text key={d.label + i} x={padLeft + band * i + band / 2} y={height - 6}
              textAnchor="middle" className="fill-muted text-2xs">
              {d.label}
            </text>
          )
        })}
      </svg>

      {hover?.x != null && (
        <Tooltip x={hover.x} y={hover.y} width={width}>
          <span className="mb-1 block font-semibold">
            {data[hover.index].sublabel ?? data[hover.index].label}
          </span>
          {series.map((s, si) => (
            <TooltipRow key={s.key} color={s.color} label={s.label}
              value={(data[hover.index].values[si] ?? 0).toLocaleString()} />
          ))}
          <TooltipRow label="Total" value={totals[hover.index].toLocaleString()} />
        </Tooltip>
      )}
    </div>
  )
}

// ---------------------------------------------------------------------------
// Area — revenue over the year
// ---------------------------------------------------------------------------

/**
 * Single series, so no legend: the card title says what is plotted. The mean is drawn as
 * a reference line and the best month is the one direct label.
 * @param data [{ label, value, sublabel? }]
 */
export function AreaTrend({
  data, height = 250, color = 'var(--color-viz-revenue)',
  formatValue = (v) => v, formatTick = (v) => v, ariaLabel = 'Trend chart',
}) {
  const [ref, width] = useWidth()
  const [hover, setHover] = useState(null)
  const gradientId = useRef(`grad-${Math.random().toString(36).slice(2, 9)}`).current

  const padLeft = 42
  const padRight = 14
  const padTop = 18
  const axisH = 22
  const plotW = Math.max(40, width - padLeft - padRight)
  const plotH = height - padTop - axisH

  const values = data.map((d) => d.value)
  const scale = niceScale(Math.max(...values, 0))
  const stepX = plotW / Math.max(1, data.length - 1)
  const x = (i) => padLeft + i * stepX
  const y = (v) => padTop + plotH - (v / scale.max) * plotH
  const points = data.map((d, i) => [x(i), y(d.value)])
  const line = smoothPath(points, padTop, padTop + plotH)
  const area = `${line} L ${x(data.length - 1)} ${padTop + plotH} L ${padLeft} ${padTop + plotH} Z`

  const nonZero = values.filter((v) => v > 0)
  const mean = nonZero.length ? nonZero.reduce((a, b) => a + b, 0) / nonZero.length : 0
  const peak = values.indexOf(Math.max(...values))
  const last = data.length - 1

  const track = useCallback((e) => {
    const box = e.currentTarget.getBoundingClientRect()
    const px = e.clientX - box.left
    const i = Math.min(data.length - 1, Math.max(0, Math.round((px - padLeft) / stepX)))
    setHover({ index: i, x: px, y: e.clientY - box.top })
  }, [data.length, stepX])

  return (
    <div ref={ref} className="relative w-full">
      <svg width={width} height={height} role="img" aria-label={ariaLabel} className="block overflow-visible"
        onMouseMove={track} onMouseLeave={() => setHover(null)}>
        <defs>
          <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor={color} stopOpacity="0.22" />
            <stop offset="100%" stopColor={color} stopOpacity="0.02" />
          </linearGradient>
        </defs>

        {scale.values.map((v) => (
          <g key={v}>
            <line x1={padLeft} x2={width - padRight} y1={y(v)} y2={y(v)}
              stroke="var(--color-grid)" strokeWidth="1" />
            <text x={padLeft - 8} y={y(v) + 4} textAnchor="end"
              className="fill-muted text-2xs tabular-nums">{formatTick(v)}</text>
          </g>
        ))}

        <path d={area} fill={`url(#${gradientId})`} />
        <path d={line} fill="none" stroke={color} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />

        {mean > 0 && (
          <>
            <line x1={padLeft} x2={width - padRight} y1={y(mean)} y2={y(mean)}
              stroke="var(--color-axis)" strokeWidth="1" />
            <text x={width - padRight} y={y(mean) - 6} textAnchor="end"
              className="fill-muted text-2xs">avg {formatTick(Math.round(mean))}</text>
          </>
        )}

        {values[peak] > 0 && (
          <text x={x(peak)} y={y(values[peak]) - 12} textAnchor={peak > data.length - 3 ? 'end' : 'middle'}
            className="fill-ink text-xs font-bold tabular-nums">
            {formatValue(values[peak])}
          </text>
        )}

        {hover && (
          <line x1={x(hover.index)} x2={x(hover.index)} y1={padTop} y2={padTop + plotH}
            stroke="var(--color-axis)" strokeWidth="1" />
        )}

        {/* End marker and hovered point carry a 2px ring in the card colour. */}
        <circle cx={x(last)} cy={y(values[last])} r="4.5" fill={color}
          stroke="var(--color-card)" strokeWidth="2" />
        {hover && (
          <circle cx={x(hover.index)} cy={y(values[hover.index])} r="5" fill={color}
            stroke="var(--color-card)" strokeWidth="2" />
        )}

        <line x1={padLeft} x2={width - padRight} y1={padTop + plotH} y2={padTop + plotH}
          stroke="var(--color-axis)" strokeWidth="1" />

        {data.map((d, i) => {
          const every = Math.ceil((data.length * 30) / Math.max(1, plotW))
          if (i % every !== 0 && i !== last) return null
          return (
            <text key={d.label + i} x={x(i)} y={height - 6} textAnchor="middle"
              className="fill-muted text-2xs">{d.label}</text>
          )
        })}
      </svg>

      {hover && (
        <Tooltip x={hover.x} y={hover.y} width={width}>
          <span className="mb-1 block font-semibold">{data[hover.index].sublabel ?? data[hover.index].label}</span>
          <TooltipRow color={color} label="Revenue" value={formatValue(values[hover.index])} />
        </Tooltip>
      )}
    </div>
  )
}

// ---------------------------------------------------------------------------
// Meter — talk-time balance
// ---------------------------------------------------------------------------

/**
 * Half-donut. The fill carries severity (normal → warning → over) and the track is a
 * lighter step of the same ramp, so the state reads across the whole arc.
 */
export function GaugeMeter({ used, allowance, height = 172, caption, unit = 'min left' }) {
  const [ref, width] = useWidth(300)
  const ratio = allowance > 0 ? Math.min(1, used / allowance) : 0
  const over = allowance > 0 && used > allowance

  const size = Math.min(width, height * 2)
  const cx = width / 2
  const stroke = 16
  const r = Math.max(20, size / 2.22 - stroke)
  const cy = height - 14
  const arc = (t) => {
    const a = Math.PI * (1 - t)
    return [cx + r * Math.cos(a), cy - r * Math.sin(a)]
  }
  const [sx, sy] = arc(0)
  const [ex, ey] = arc(1)
  const [px, py] = arc(ratio || 0.0001)

  const fill = over ? 'var(--color-crit)' : ratio > 0.8 ? 'var(--color-warn)' : 'var(--color-viz-fill)'
  const remaining = Math.max(0, allowance - used)

  return (
    <div ref={ref} className="relative w-full">
      <svg width={width} height={height} role="img" className="block"
        aria-label={allowance > 0
          ? `${used} of ${allowance} minutes used this period`
          : `${used} minutes used this period`}>
        <path d={`M ${sx} ${sy} A ${r} ${r} 0 0 1 ${ex} ${ey}`}
          fill="none" stroke="var(--color-viz-track)" strokeWidth={stroke} strokeLinecap="round" />
        {ratio > 0 && (
          /* large-arc-flag stays 0: the whole track is a half turn, so no sweep of it
             ever exceeds 180° and a 1 here would draw the long way round. */
          <path d={`M ${sx} ${sy} A ${r} ${r} 0 0 1 ${px} ${py}`}
            fill="none" stroke={fill} strokeWidth={stroke} strokeLinecap="round" />
        )}
        <text x={cx} y={cy - 34} textAnchor="middle" className="fill-ink text-3xl font-bold">
          {allowance > 0 ? remaining.toLocaleString() : used.toLocaleString()}
        </text>
        <text x={cx} y={cy - 14} textAnchor="middle" className="fill-muted text-xs">
          {allowance > 0 ? unit : 'minutes used'}
        </text>
      </svg>
      {caption && <p className="-mt-1 text-center text-xs text-muted">{caption}</p>}
    </div>
  )
}

/** Straight meter for the stat tiles. Same ramp for track and fill. */
export function BarMeter({ ratio, tone = 'var(--color-viz-fill)', className = '' }) {
  const pct = Math.max(0, Math.min(1, Number.isFinite(ratio) ? ratio : 0)) * 100
  return (
    <div className={`h-1.5 w-full overflow-hidden rounded-pill bg-[var(--color-viz-track)] ${className}`}>
      <div className="h-full rounded-pill transition-[width] duration-500"
        style={{ width: `${pct}%`, background: tone }} />
    </div>
  )
}

// ---------------------------------------------------------------------------
// Sparkline — the trend inside a stat tile
// ---------------------------------------------------------------------------

export function Sparkline({ values, height = 34, color = 'var(--color-viz-fill)' }) {
  const [ref, width] = useWidth(120)
  if (!values?.length) return <div ref={ref} style={{ height }} />

  // A series of nothing is drawn as a quiet baseline, not a confident coloured rule.
  const flat = values.every((v) => !v)
  if (flat) color = 'var(--color-axis)'

  const max = Math.max(...values, 1)
  const min = Math.min(...values, 0)
  const span = max - min || 1
  const stepX = width / Math.max(1, values.length - 1)
  const pts = values.map((v, i) => [i * stepX, height - 3 - ((v - min) / span) * (height - 6)])
  const line = smoothPath(pts, 3, height - 3)

  return (
    <div ref={ref} className="w-full">
      <svg width={width} height={height} aria-hidden="true" className="block">
        <path d={`${line} L ${width} ${height} L 0 ${height} Z`} fill={color} opacity="0.1" />
        <path d={line} fill="none" stroke={color} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </div>
  )
}

// ---------------------------------------------------------------------------
// Heat strip — busiest hours
// ---------------------------------------------------------------------------

/** One hue, more-is-darker: a sequential encoding of call volume by hour. */
export function HourHeat({ hours, labelFor = (h) => h }) {
  const [hover, setHover] = useState(null)
  const max = Math.max(...hours.map((h) => h.value), 1)

  useEffect(() => {
    if (!hover) return
    const clear = () => setHover(null)
    window.addEventListener('scroll', clear, true)
    return () => window.removeEventListener('scroll', clear, true)
  }, [hover])

  return (
    <div>
      <div className="flex gap-[2px]">
        {hours.map((h) => {
          const t = h.value / max
          return (
            <button
              key={h.hour}
              type="button"
              onMouseEnter={() => setHover(h)}
              onMouseLeave={() => setHover(null)}
              onFocus={() => setHover(h)}
              onBlur={() => setHover(null)}
              aria-label={`${labelFor(h.hour)}: ${h.value} calls`}
              className="h-9 flex-1 rounded-[4px] outline-none ring-offset-2 focus-visible:ring-2 focus-visible:ring-ink"
              style={{
                background: h.value === 0 ? 'var(--color-viz-track)' : 'var(--color-viz-fill)',
                opacity: h.value === 0 ? 1 : 0.25 + t * 0.75,
              }}
            />
          )
        })}
      </div>
      <div className="mt-1.5 flex justify-between text-2xs text-muted">
        <span>12a</span><span>6a</span><span>12p</span><span>6p</span><span>11p</span>
      </div>
      <p className="mt-2 min-h-[18px] text-xs text-ink-soft">
        {hover
          ? <><span className="font-semibold">{labelFor(hover.hour)}</span> · {hover.value} call{hover.value === 1 ? '' : 's'}</>
          : 'Hover an hour to see its call count.'}
      </p>
    </div>
  )
}
