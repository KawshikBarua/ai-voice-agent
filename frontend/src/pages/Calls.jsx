import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '../api/client'
import { Card, Chip, Avatar, EmptyState, PageHeader, PillButton, compactAction } from '../components/ui'

export default function Calls() {
  const qc = useQueryClient()
  const [expanded, setExpanded] = useState(null)
  const { data, isLoading } = useQuery({
    queryKey: ['calls'],
    queryFn: () => api.get('/calls', { params: { pageSize: 50 } }).then(unwrap),
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
    },
  })

  const dur = (s) => (s >= 60 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${s}s`)
  const tone = (status) => (status === 'Missed' ? 'red' : status === 'Transferred' ? 'cream' : 'mint')

  return (
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

        {isLoading && <EmptyState message="Loading calls…" />}
        {!isLoading && !data?.items?.length && (
          <EmptyState message="No calls yet. Calls handled by the AI agent appear here — use “Sync from Retell” if a call is missing." />
        )}
        <div className="space-y-3">
          {data?.items?.map((c, i) => {
            const suggestion = suggestionByCall[c.id]
            return (
              <div key={c.id} className="rounded-2xl bg-panel p-4">
                <button className="flex w-full items-center gap-3 text-left sm:gap-4"
                  onClick={() => setExpanded(expanded === c.id ? null : c.id)}>
                  <Avatar name={c.customerName ?? '??'} tone={['lavender', 'mint', 'cream'][i % 3]} />
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-bold sm:text-base">{c.customerName ?? c.fromNumber}</p>
                    <p className="truncate text-xs text-ink-soft">
                      {new Date(c.startedAt).toLocaleString()} · {dur(c.durationSeconds)}
                    </p>
                  </div>
                  {/* The status is the one chip that must always be visible; "Action suggested" is
                      the longer of the two and stands down first on a narrow screen. */}
                  {suggestion && <span className="hidden shrink-0 sm:inline"><Chip tone="lavender">Action suggested</Chip></span>}
                  {suggestion && <span className="shrink-0 sm:hidden"><Chip tone="lavender">Action</Chip></span>}
                  <span className="shrink-0"><Chip tone={tone(c.status)}>{c.status}</Chip></span>
                </button>
                {expanded === c.id && (
                  <div className="mt-4 space-y-3 border-t border-line pt-4">
                    {suggestion && <SuggestionCard suggestion={suggestion} />}
                    {c.summary && (
                      <div className="rounded-2xl bg-card p-3.5">
                        <p className="mb-1 text-xs font-semibold text-muted">AI Summary</p>
                        <p className="text-sm">{c.summary}</p>
                      </div>
                    )}
                    {c.transcript ? (
                      <div className="rounded-2xl bg-card p-3.5">
                        <p className="mb-1 text-xs font-semibold text-muted">Transcript</p>
                        <p className="whitespace-pre-wrap text-sm text-ink-soft">{c.transcript}</p>
                      </div>
                    ) : (
                      <p className="text-xs text-muted">No transcript available for this call.</p>
                    )}
                    {c.recordingUrl && (
                      <audio controls src={c.recordingUrl} className="w-full" />
                    )}
                  </div>
                )}
              </div>
            )
          })}
        </div>
      </Card>
    </div>
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
