import { Fragment, useState } from 'react'
import { useInfiniteQuery, useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { AnimatePresence, MotionConfig, motion } from 'framer-motion'
import { api, unwrap } from '../api/client'
import {
  Card, Chip, Chevron, Avatar, EmptyState, PageHeader, PillButton, compactAction,
  tableHead, tableCell, tableScroller,
} from '../components/ui'
import { DESKTOP, useLoadMore, useMediaQuery } from '../lib/hooks'
import { useBusinessTimeZone } from '../lib/businessTime'
import { formatInZone } from '../lib/format'

const PAGE_SIZE = 50
const TONES = ['lavender', 'mint', 'cream']

const dur = (s) => (s >= 60 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${s}s`)
const tone = (status) => (status === 'Missed' ? 'red' : status === 'Transferred' ? 'cream' : 'mint')

/** The opened call. `call` is the full record — the list carries only the summary row, so the
 *  transcript, AI summary and recording arrive here, for this one call, when it is opened. */
function CallDetail({ call, suggestion }) {
  return (
    <div className="space-y-3">
      {suggestion && <SuggestionCard suggestion={suggestion} />}
      {!call ? (
        <p className="animate-pulse text-xs text-muted" role="status">Loading call…</p>
      ) : (
        <>
          {call.summary && (
            <div className="rounded-2xl bg-card p-3.5">
              <p className="mb-1 text-xs font-semibold text-muted">AI Summary</p>
              <p className="text-sm">{call.summary}</p>
            </div>
          )}
          {call.transcript ? (
            <div className="rounded-2xl bg-card p-3.5">
              <p className="mb-1 text-xs font-semibold text-muted">Transcript</p>
              <p className="whitespace-pre-wrap text-sm text-ink-soft">{call.transcript}</p>
            </div>
          ) : (
            <p className="text-xs text-muted">No transcript available for this call.</p>
          )}
          {call.recordingUrl && (
            <audio controls src={call.recordingUrl} className="w-full" />
          )}
        </>
      )}
    </div>
  )
}

function CallTable({ calls, loading, expanded, onToggle, detail, suggestionByCall }) {
  const timeZone = useBusinessTimeZone()
  return (
    <table className="w-full table-fixed border-separate border-spacing-0 text-left text-sm">
      <colgroup>
        <col />
        <col className="w-40" />
        <col className="w-48" />
        <col className="w-24" />
        <col className="w-56" />
        <col className="w-12" />
      </colgroup>
      <thead className="sticky top-0 z-10 bg-panel/90 backdrop-blur">
        <tr>
          <th scope="col" className={tableHead}>Caller</th>
          <th scope="col" className={tableHead}>Phone</th>
          <th scope="col" className={tableHead}>Date &amp; time</th>
          <th scope="col" className={`${tableHead} text-right`}>Duration</th>
          <th scope="col" className={tableHead}>Status</th>
          <th scope="col" className={tableHead}><span className="sr-only">Details</span></th>
        </tr>
      </thead>
      <tbody>
        {loading && Array.from({ length: 8 }, (_, i) => (
          <tr key={i} className="animate-pulse">
            <td className={tableCell}>
              <div className="flex items-center gap-3">
                <span className="h-8 w-8 shrink-0 rounded-full bg-panel" />
                <span className="h-3 w-32 rounded-full bg-panel" />
              </div>
            </td>
            {[24, 32, 10, 18].map((w, j) => (
              <td key={j} className={tableCell}><span className="block h-3 rounded-full bg-panel" style={{ width: `${w * 4}px`, maxWidth: '100%' }} /></td>
            ))}
            <td className={tableCell} />
          </tr>
        ))}

        {calls.map((c, i) => {
          const open = expanded === c.id
          const suggestion = suggestionByCall[c.id]
          return (
            <Fragment key={c.id}>
              {/* Opacity only: a transform on a <tr> is still unreliable in Safari. The stagger
                  restarts with each page, so a newly loaded batch ripples in on its own. */}
              <motion.tr
                initial={{ opacity: 0 }}
                animate={{ opacity: 1 }}
                transition={{ duration: 0.3, delay: Math.min(i % PAGE_SIZE, 12) * 0.02 }}
                onClick={() => onToggle(c.id)}
                className={`cursor-pointer transition-colors duration-150 hover:bg-panel ${open ? 'bg-panel' : ''}`}
              >
                <td className={tableCell}>
                  {/* No handler of its own — the click bubbles to the row. It is here so the
                      row can be reached and opened from the keyboard. */}
                  <button type="button" aria-expanded={open} className="flex w-full min-w-0 items-center gap-3 text-left">
                    <Avatar name={c.customerName ?? '??'} size="sm" tone={TONES[c.id % 3]} />
                    <span className="truncate font-semibold">{c.customerName ?? 'Unknown caller'}</span>
                  </button>
                </td>
                <td className={`${tableCell} truncate tabular-nums text-ink-soft`}>{c.fromNumber}</td>
                <td className={`${tableCell} truncate text-ink-soft`}>{formatInZone(c.startedAt, timeZone)}</td>
                <td className={`${tableCell} text-right tabular-nums`}>{dur(c.durationSeconds)}</td>
                <td className={tableCell}>
                  <span className="flex items-center gap-1.5">
                    <Chip tone={tone(c.status)}>{c.status}</Chip>
                    {suggestion && <Chip tone="lavender">Action suggested</Chip>}
                  </span>
                </td>
                <td className={tableCell}><Chevron open={open} /></td>
              </motion.tr>
              <tr>
                <td colSpan={6} className="p-0">
                  <AnimatePresence initial={false}>
                    {open && (
                      <motion.div
                        initial={{ height: 0, opacity: 0 }}
                        animate={{ height: 'auto', opacity: 1 }}
                        exit={{ height: 0, opacity: 0 }}
                        transition={{ duration: 0.22, ease: 'easeOut' }}
                        className="overflow-hidden"
                      >
                        <div className="border-b border-line bg-panel p-4">
                          <CallDetail call={detail} suggestion={suggestion} />
                        </div>
                      </motion.div>
                    )}
                  </AnimatePresence>
                </td>
              </tr>
            </Fragment>
          )
        })}
      </tbody>
    </table>
  )
}

function CallCards({ calls, expanded, onToggle, detail, suggestionByCall }) {
  const timeZone = useBusinessTimeZone()
  return (
    <div className="space-y-3">
      {calls.map((c) => {
        const suggestion = suggestionByCall[c.id]
        return (
          <div key={c.id} className="rounded-2xl bg-panel p-4">
            <button className="flex w-full items-center gap-3 text-left sm:gap-4"
              aria-expanded={expanded === c.id}
              onClick={() => onToggle(c.id)}>
              <Avatar name={c.customerName ?? '??'} tone={TONES[c.id % 3]} />
              <div className="min-w-0 flex-1">
                <p className="truncate text-sm font-bold sm:text-base">{c.customerName ?? c.fromNumber}</p>
                <p className="truncate text-xs text-ink-soft">
                  {formatInZone(c.startedAt, timeZone)} · {dur(c.durationSeconds)}
                </p>
              </div>
              {/* The status is the one chip that must always be visible; "Action suggested" is
                  the longer of the two and stands down first on a narrow screen. */}
              {suggestion && <span className="hidden shrink-0 sm:inline"><Chip tone="lavender">Action suggested</Chip></span>}
              {suggestion && <span className="shrink-0 sm:hidden"><Chip tone="lavender">Action</Chip></span>}
              <span className="shrink-0"><Chip tone={tone(c.status)}>{c.status}</Chip></span>
            </button>
            {expanded === c.id && (
              <div className="mt-4 border-t border-line pt-4">
                <CallDetail call={detail} suggestion={suggestion} />
              </div>
            )}
          </div>
        )
      })}
    </div>
  )
}

export default function Calls() {
  const qc = useQueryClient()
  const [expanded, setExpanded] = useState(null)
  const isDesktop = useMediaQuery(DESKTOP)

  // Pages are fetched by cursor as the list is scrolled, so the first screen costs the same
  // however long the call history is.
  const list = useInfiniteQuery({
    queryKey: ['calls', 'list'],
    queryFn: ({ pageParam, signal }) =>
      api.get('/calls', { params: { cursor: pageParam ?? undefined, pageSize: PAGE_SIZE }, signal }).then(unwrap),
    initialPageParam: null,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  })
  const { data, isLoading, isError, isFetchingNextPage } = list
  const calls = data?.pages.flatMap((p) => p.items) ?? []

  // Under ['calls'] so a sync, which invalidates that key, refreshes an open call as well.
  const { data: detail } = useQuery({
    queryKey: ['calls', 'detail', expanded],
    enabled: !!expanded,
    queryFn: () => api.get(`/calls/${expanded}`).then(unwrap),
  })

  // Booking actions the AI inferred from transferred-call transcripts, awaiting confirmation.
  const { data: suggestions } = useQuery({
    queryKey: ['call-suggestions'],
    queryFn: () => api.get('/calls/suggestions').then(unwrap),
  })
  const suggestionByCall = {}
  for (const s of suggestions ?? []) suggestionByCall[s.callLogId] = s

  // Webhooks can be missed (tunnel down, API restarting) — this reconciles from Retell.
  const sync = useMutation({
    mutationFn: () => api.post('/calls/sync').then((r) => r.data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['calls'] })
      qc.invalidateQueries({ queryKey: ['sidebar-counts'] })
      qc.invalidateQueries({ queryKey: ['dashboard'] })
      // A sync can correct call lengths, which moves the minutes; and it is a sync, so the
      // dashboard's "Synced … ago" should say so.
      qc.invalidateQueries({ queryKey: ['billing-usage'] })
      qc.invalidateQueries({ queryKey: ['call-sync-status'] })
    },
  })

  const { scrollerRef, sentinelRef } = useLoadMore(list, { contained: isDesktop, count: calls.length })

  const toggle = (id) => setExpanded((cur) => (cur === id ? null : id))
  const empty = !isLoading && !isError && calls.length === 0
  const rows = { calls, expanded, onToggle: toggle, detail, suggestionByCall }

  return (
    <MotionConfig reducedMotion="user">
      <div className="mx-auto max-w-5xl">
        <PageHeader
          title="Calls"
          action={
            <PillButton variant="outline" className={compactAction}
              onClick={() => sync.mutate()} disabled={sync.isPending}>
              {sync.isPending ? 'Syncing…' : 'Sync'}
            </PillButton>
          }
        />

        <Card>
          {sync.isSuccess && (
            <p className="mb-3 rounded-2xl bg-mint px-4 py-2.5 text-sm font-medium">{sync.data?.message}</p>
          )}
          {sync.isError && (
            <p className="mb-3 rounded-2xl bg-danger-soft px-4 py-2.5 text-sm font-medium text-danger">
              {sync.error?.response?.data?.message ?? 'Could not reach Retell.'}
            </p>
          )}

          {isError && <EmptyState message="Couldn't load calls. Please try again." />}
          {empty && (
            <EmptyState message="No calls yet. Calls handled by the AI agent appear here — use “Sync from Retell” if a call is missing." />
          )}
          {isLoading && !isDesktop && <EmptyState message="Loading calls…" />}

          {/* On a desktop this box is the scroller: the header row sticks to its top and only
              the rows move. On a phone it has no height of its own and the page scrolls. */}
          <div ref={scrollerRef} className={isDesktop && !empty && !isError ? tableScroller : ''}>
            {isDesktop
              ? !empty && !isError && <CallTable loading={isLoading} {...rows} />
              : <CallCards {...rows} />}

            <div ref={sentinelRef} aria-hidden="true" />
            {isFetchingNextPage && (
              <p className="animate-pulse py-4 text-center text-xs text-muted" role="status">Loading more…</p>
            )}
          </div>
        </Card>
      </div>
    </MotionConfig>
  )
}

// Local datetime -> value for <input type="datetime-local"> ("yyyy-MM-ddTHH:mm").
function toLocalInput(value) {
  if (!value) return ''
  // Suggestion times are naive local wall-clock; strip any zone and seconds.
  return String(value).replace('Z', '').slice(0, 16)
}

function SuggestionCard({ suggestion }) {
  const qc = useQueryClient()
  const [form, setForm] = useState({
    action: suggestion.action,
    name: suggestion.customerName ?? '',
    phone: suggestion.phone ?? '',
    service: suggestion.serviceName ?? '',
    startAtLocal: toLocalInput(suggestion.startAtLocal),
  })

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ['call-suggestions'] })
    qc.invalidateQueries({ queryKey: ['appointments'] })
    qc.invalidateQueries({ queryKey: ['dashboard'] })
    qc.invalidateQueries({ queryKey: ['sidebar-counts'] })
  }

  const confirm = useMutation({
    mutationFn: () => api.post(`/calls/suggestions/${suggestion.id}/confirm`, form).then((r) => r.data),
    onSuccess: invalidate,
  })
  const dismiss = useMutation({
    mutationFn: () => api.post(`/calls/suggestions/${suggestion.id}/dismiss`).then((r) => r.data),
    onSuccess: invalidate,
  })

  const confTone = suggestion.confidence === 'high' ? 'mint' : suggestion.confidence === 'medium' ? 'cream' : 'red'
  const set = (k) => (e) => setForm({ ...form, [k]: e.target.value })
  const busy = confirm.isPending || dismiss.isPending
  const needsTime = form.action === 'Book' || form.action === 'Reschedule'

  const field = 'w-full rounded-xl border border-line bg-card px-3 py-2 text-sm'
  const label = 'mb-1 block text-xs font-semibold text-muted'

  return (
    <div className="rounded-2xl border border-lavender bg-card p-3.5">
      <div className="mb-2 flex items-center gap-2">
        <Chip tone="lavender">Suggested action</Chip>
        <Chip tone={confTone}>{suggestion.confidence} confidence</Chip>
        <span className="text-xs text-muted">This call was transferred — confirm before it takes effect.</span>
      </div>

      {suggestion.reasoning && (
        <p className="mb-3 text-xs text-ink-soft">{suggestion.reasoning}</p>
      )}

      <div className="grid grid-cols-2 gap-2.5">
        <div>
          <label className={label}>Action</label>
          <select className={field} value={form.action} onChange={set('action')}>
            <option value="Book">Book</option>
            <option value="Reschedule">Reschedule</option>
            <option value="Cancel">Cancel</option>
          </select>
        </div>
        <div>
          <label className={label}>Service</label>
          <input className={field} value={form.service} onChange={set('service')} placeholder="Service name" />
        </div>
        <div>
          <label className={label}>Customer name</label>
          <input className={field} value={form.name} onChange={set('name')} placeholder="Name" />
        </div>
        <div>
          <label className={label}>Phone</label>
          <input className={field} value={form.phone} onChange={set('phone')} placeholder="Phone" />
        </div>
        {needsTime && (
          <div className="col-span-2">
            <label className={label}>Start time</label>
            <input type="datetime-local" className={field} value={form.startAtLocal} onChange={set('startAtLocal')} />
          </div>
        )}
      </div>

      {(confirm.isError || dismiss.isError) && (
        <p className="mt-2.5 rounded-xl bg-danger-soft px-3 py-2 text-xs font-medium text-danger">
          {(confirm.error ?? dismiss.error)?.response?.data?.message ?? 'Could not complete the action.'}
        </p>
      )}
      {confirm.isSuccess && (
        <p className="mt-2.5 rounded-xl bg-mint px-3 py-2 text-xs font-medium">{confirm.data?.message}</p>
      )}

      <div className="mt-3 flex gap-2">
        <PillButton onClick={() => confirm.mutate()} disabled={busy}>
          {confirm.isPending ? 'Confirming…' : `Confirm ${form.action.toLowerCase()}`}
        </PillButton>
        <PillButton variant="outline" onClick={() => dismiss.mutate()} disabled={busy}>
          {dismiss.isPending ? 'Dismissing…' : 'Dismiss'}
        </PillButton>
      </div>
    </div>
  )
}
