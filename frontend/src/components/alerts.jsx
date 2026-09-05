import { useCallback, useEffect, useRef, useState } from 'react'
import { AnimatePresence, motion } from 'framer-motion'
import { useNavigate } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '../api/client'
import { PillButton, Chip } from './ui'

/**
 * Things that happened while nobody was looking.
 *
 * Two halves that answer the same problem from opposite ends. The service worker delivers a push
 * to the phone when the app is shut; this queue holds the same events in the app, unacknowledged,
 * for the far more common case of a notification that arrived and was missed. An emergency
 * booking that nobody has opened stays here and keeps being re-sent by the server until somebody
 * says they have seen it.
 */

/* ------------------------------------------------------------------ live stream */

const STREAM_POLL_MS = 20_000

/**
 * New alerts, from whichever channel gets there first.
 *
 * Two sources, deliberately. The service worker is the fast one: a push that woke the phone is the
 * same message that updates an open tab, so a device with notifications on sees a booking land the
 * instant it happens. But push needs permission, a subscription, and a browser that supports it —
 * and until all three are true the worker never fires, which used to mean an app sitting open on
 * screen showed nothing at all when a call booked somebody in.
 *
 * So the queue is polled as well. It costs one small request every {@link STREAM_POLL_MS}, it works
 * on every device with the app open, and both paths are deduplicated by alert id so a device with
 * push on does not toast twice.
 */
export function useAlertStream() {
  const [toasts, setToasts] = useState([])
  const queryClient = useQueryClient()
  const navigate = useNavigate()

  // Every alert id this tab has already shown. The first poll only fills it — a backlog of things
  // that happened before the app was opened belongs in the attention queue, not in a stack of
  // toasts across the screen at sign-in.
  const seen = useRef(null)

  const show = useCallback((alerts) => {
    const fresh = []
    for (const alert of alerts) {
      if (alert.id != null && seen.current.has(alert.id)) continue
      if (alert.id != null) seen.current.add(alert.id)
      fresh.push({ ...alert, key: `${alert.id ?? 'x'}-${Date.now()}-${fresh.length}` })
    }
    if (fresh.length === 0) return false

    setToasts((current) => [...current, ...fresh])
    return true
  }, [])

  // Whatever is on screen is now out of date — the diary just changed underneath it. The queue
  // itself is left out: the poll below already holds the current copy, and invalidating the key it
  // reads would send it round again for the same answer.
  const invalidate = useCallback(() => {
    for (const key of [['appointments'], ['dashboard'], ['sidebar-counts']])
      queryClient.invalidateQueries({ queryKey: key })
  }, [queryClient])

  /* ---- the push path ---- */
  useEffect(() => {
    if (!('serviceWorker' in navigator)) return undefined

    const onMessage = (event) => {
      const message = event.data
      if (!message) return

      // Tapping a notification focuses this tab; routing here keeps it a route change rather
      // than a full reload of the bundle.
      if (message.type === 'frontly-navigate') {
        navigate(message.url ?? '/')
        return
      }

      if (message.type !== 'frontly-alert') return

      // A push can beat the first poll, in which case there is no baseline yet. Starting one here
      // rather than skipping the toast: this alert arrived while the tab was open, so it is new
      // by definition.
      seen.current ??= new Set()
      if (show([message.alert ?? {}])) {
        invalidate()
        // Unlike the poll, a push carries only the one alert — the queue still has to be re-read.
        queryClient.invalidateQueries({ queryKey: ['alerts'] })
      }
    }

    navigator.serviceWorker.addEventListener('message', onMessage)
    return () => navigator.serviceWorker.removeEventListener('message', onMessage)
  }, [navigate, show, invalidate, queryClient])

  /* ---- the polling path ---- */
  const { data: pending } = useQuery({
    queryKey: ['alerts'],
    queryFn: () => api.get('/notifications/alerts', { params: { pendingOnly: true } }).then(unwrap),
    refetchInterval: STREAM_POLL_MS,
    // A tab in the background is not being watched; it catches up on the next look at it.
    refetchIntervalInBackground: false,
  })

  useEffect(() => {
    if (!pending) return

    if (seen.current === null) {
      seen.current = new Set(pending.map((a) => a.id))
      return
    }
    if (show(pending)) invalidate()
  }, [pending, show, invalidate])

  return { toasts, dismiss: (key) => setToasts((c) => c.filter((t) => t.key !== key)) }
}

/** Corner toasts for the stream above. Routine ones fade; urgent ones wait to be dismissed. */
export function Toasts({ toasts, dismiss }) {
  return (
    <div className="pointer-events-none fixed bottom-4 right-4 z-50 flex w-[min(22rem,calc(100vw-2rem))] flex-col gap-2">
      <AnimatePresence initial={false}>
        {toasts.map((toast) => (
          <Toast key={toast.key} toast={toast} onDismiss={() => dismiss(toast.key)} />
        ))}
      </AnimatePresence>
    </div>
  )
}

function Toast({ toast, onDismiss }) {
  const urgent = toast.severity === 'Urgent'
  const navigate = useNavigate()

  useEffect(() => {
    // An urgent toast is never taken off screen on a timer: the whole point is that it is still
    // there when somebody walks back to the counter.
    if (urgent) return undefined
    const timer = setTimeout(onDismiss, 8000)
    return () => clearTimeout(timer)
  }, [urgent, onDismiss])

  return (
    <motion.div
      layout
      initial={{ opacity: 0, y: 12, scale: 0.98 }}
      animate={{ opacity: 1, y: 0, scale: 1 }}
      exit={{ opacity: 0, y: 8, scale: 0.98 }}
      transition={{ duration: 0.2, ease: 'easeOut' }}
      className={`pointer-events-auto rounded-card border p-3.5 shadow-lg ${
        urgent ? 'border-danger/40 bg-danger-soft' : 'border-line bg-card'
      }`}
    >
      <div className="flex items-start gap-2.5">
        <div className="min-w-0 flex-1">
          <p className={`text-sm font-bold ${urgent ? 'text-danger' : 'text-ink'}`}>{toast.title}</p>
          <p className="mt-0.5 text-xs text-ink-soft">{toast.body}</p>
        </div>
        <button
          onClick={onDismiss}
          aria-label="Dismiss"
          className="grid h-6 w-6 shrink-0 place-items-center rounded-full text-muted transition hover:bg-panel hover:text-ink"
        >
          <svg viewBox="0 0 24 24" className="h-3.5 w-3.5" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round">
            <path d="M18 6 6 18M6 6l12 12" />
          </svg>
        </button>
      </div>

      {toast.url && (
        <button
          onClick={() => { navigate(toast.url); onDismiss() }}
          className="mt-2 text-xs font-semibold text-ink underline underline-offset-2"
        >
          Open
        </button>
      )}
    </motion.div>
  )
}

/* ------------------------------------------------------------- attention queue */

const sinceLabel = (iso) => {
  const minutes = Math.round((Date.now() - new Date(iso).getTime()) / 60000)
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${minutes} min ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours} hour${hours === 1 ? '' : 's'} ago`
  const days = Math.round(hours / 24)
  return `${days} day${days === 1 ? '' : 's'} ago`
}

/**
 * The unacknowledged queue.
 *
 * This is the half that does not depend on anyone having notifications switched on, or on a
 * notification having been seen. It is deliberately loud when something urgent is waiting and
 * completely absent when nothing is — a card that is always on screen stops being read.
 */
export function AttentionCard() {
  const queryClient = useQueryClient()

  const { data: alerts } = useQuery({
    queryKey: ['alerts'],
    queryFn: () => api.get('/notifications/alerts', { params: { pendingOnly: true } }).then(unwrap),
    // A backstop for a device with notifications off; the push stream updates this the instant
    // anything happens on a device that has them on.
    refetchInterval: 60_000,
  })

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['alerts'] })
    queryClient.invalidateQueries({ queryKey: ['sidebar-counts'] })
  }

  const acknowledge = useMutation({
    mutationFn: (id) => api.post(`/notifications/alerts/${id}/acknowledge`),
    onSuccess: refresh,
  })
  const acknowledgeAll = useMutation({
    mutationFn: () => api.post('/notifications/alerts/acknowledge-all'),
    onSuccess: refresh,
  })

  if (!alerts?.length) return null

  const urgent = alerts.filter((a) => a.severity === 'Urgent')

  return (
    <motion.section
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.28, ease: 'easeOut' }}
      className={`mb-4 rounded-card border p-4 shadow-sm sm:p-5 ${
        urgent.length > 0 ? 'border-danger/40 bg-danger-soft' : 'border-line bg-card'
      }`}
    >
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <div>
          <h2 className="font-display text-lg font-semibold tracking-[-0.01em]">
            {urgent.length > 0 ? 'Needs your attention' : 'While you were away'}
          </h2>
          <p className="mt-0.5 text-xs text-muted">
            {urgent.length > 0
              ? `${urgent.length} of these cannot wait — you will keep being notified until they are marked as seen.`
              : 'Your AI handled these. Mark them as seen once you have looked.'}
          </p>
        </div>
        {alerts.length > 1 && (
          <PillButton variant="outline" disabled={acknowledgeAll.isPending}
            onClick={() => acknowledgeAll.mutate()}>
            {acknowledgeAll.isPending ? 'Marking…' : 'Mark all seen'}
          </PillButton>
        )}
      </div>

      <div className="space-y-2">
        {alerts.map((alert) => (
          <div key={alert.id}
            className="flex flex-wrap items-center gap-3 rounded-2xl bg-card/80 px-4 py-3">
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-bold">{alert.title}</p>
              <p className="truncate text-xs text-ink-soft">{alert.body}</p>
            </div>
            {alert.severity === 'Urgent' && <Chip tone="red">Urgent</Chip>}
            <span className="text-xs text-muted">{sinceLabel(alert.createdAt)}</span>
            <PillButton variant="light" className="!px-3 !py-1.5 !text-xs"
              disabled={acknowledge.isPending}
              onClick={() => acknowledge.mutate(alert.id)}>
              Mark seen
            </PillButton>
          </div>
        ))}
      </div>
    </motion.section>
  )
}
