import { useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '../api/client'
import { Card, CardTitle, PageHeader, PillButton, Chip, Avatar, EmptyState, compactAction } from '../components/ui'

export default function Customers() {
  const qc = useQueryClient()
  const [params] = useSearchParams()
  // Seeded from the dashboard search box (/customers?q=…)
  const [search, setSearch] = useState(() => params.get('q') ?? '')
  const [showForm, setShowForm] = useState(false)
  const [expanded, setExpanded] = useState(null)
  const [form, setForm] = useState({ name: '', phone: '', email: '', address: '' })

  const { data, isLoading } = useQuery({
    queryKey: ['customers', search],
    queryFn: () => api.get('/customers', { params: { search: search || undefined, pageSize: 50 } }).then(unwrap),
  })

  const { data: timeline } = useQuery({
    queryKey: ['timeline', expanded],
    enabled: !!expanded,
    queryFn: () => api.get(`/customers/${expanded}/timeline`).then(unwrap),
  })

  const create = useMutation({
    mutationFn: (body) => api.post('/customers', body).then(unwrap),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['customers'] })
      setShowForm(false)
      setForm({ name: '', phone: '', email: '', address: '' })
    },
  })

  const input = 'w-full rounded-2xl border border-line bg-card px-4 py-2.5 text-base outline-none focus:border-ink'
  const fmtDate = (iso) => (iso ? new Date(iso).toLocaleDateString() : '—')

  return (
    <div className="mx-auto max-w-5xl">
      <PageHeader
        title="Customers"
        action={
          <PillButton className={compactAction} onClick={() => setShowForm((v) => !v)}>
            <span className="sm:hidden">+ New</span>
            <span className="hidden sm:inline">+ New Customer</span>
          </PillButton>
        }
      />

      {showForm && (
        <Card className="mb-5">
          <CardTitle title="New customer" />
          <form
            onSubmit={(e) => { e.preventDefault(); create.mutate(form) }}
            className="grid gap-3 sm:grid-cols-2"
          >
            <input required placeholder="Full name" className={input} value={form.name}
              onChange={(e) => setForm({ ...form, name: e.target.value })} />
            <input required placeholder="Phone number" className={input} value={form.phone}
              onChange={(e) => setForm({ ...form, phone: e.target.value })} />
            <input type="email" placeholder="Email (optional)" className={input} value={form.email}
              onChange={(e) => setForm({ ...form, email: e.target.value })} />
            <input placeholder="Address (optional)" className={input} value={form.address}
              onChange={(e) => setForm({ ...form, address: e.target.value })} />
            <div className="flex gap-2 sm:col-span-2">
              <PillButton type="submit" disabled={create.isPending}>Save customer</PillButton>
              <PillButton type="button" variant="light" onClick={() => setShowForm(false)}>Cancel</PillButton>
            </div>
          </form>
        </Card>
      )}

      <Card>
        <div className="mb-4 flex items-center gap-2 rounded-pill bg-panel px-4 py-2.5">
          <svg viewBox="0 0 24 24" className="h-4 w-4 text-muted" fill="none" stroke="currentColor" strokeWidth="2">
            <circle cx="11" cy="11" r="7" /><path d="m21 21-4.3-4.3" />
          </svg>
          <input
            placeholder="Search by name, phone or email…"
            className="w-full bg-transparent text-sm outline-none placeholder:text-muted"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </div>

        {isLoading && <EmptyState message="Loading customers…" />}
        {!isLoading && !data?.items?.length && <EmptyState message="No customers found." />}

        <div className="space-y-3">
          {data?.items?.map((c, i) => (
            <div key={c.id} className="rounded-2xl bg-panel p-4">
              <button
                className="flex w-full items-center gap-3 text-left sm:gap-4"
                onClick={() => setExpanded(expanded === c.id ? null : c.id)}
              >
                <Avatar name={c.name} tone={['lavender', 'mint', 'cream'][i % 3]} />
                <div className="min-w-0 flex-1">
                  {/* Both truncated: an email long enough to wrap took the contact line to two
                      rows and left the "Returning" chip floating beside a ragged block. */}
                  <p className="truncate text-sm font-bold sm:text-base">{c.name}</p>
                  <p className="truncate text-xs text-ink-soft">{c.phone}{c.email ? ` · ${c.email}` : ''}</p>
                </div>
                {c.totalVisits > 1 && <span className="shrink-0"><Chip tone="mint">Returning</Chip></span>}
                <div className="hidden text-right sm:block">
                  <p className="text-xs text-muted">Last visit</p>
                  <p className="text-sm font-semibold">{fmtDate(c.lastVisit)}</p>
                </div>
              </button>

              {expanded === c.id && (
                <div className="mt-4 border-t border-line pt-4">
                  <div className="mb-3 grid grid-cols-3 gap-3 text-center">
                    <div className="rounded-2xl bg-card p-3">
                      <p className="text-xs text-muted">First visit</p>
                      <p className="text-sm font-bold">{fmtDate(c.firstVisit)}</p>
                    </div>
                    <div className="rounded-2xl bg-card p-3">
                      <p className="text-xs text-muted">Total visits</p>
                      <p className="text-sm font-bold">{c.totalVisits}</p>
                    </div>
                    <div className="rounded-2xl bg-card p-3">
                      <p className="text-xs text-muted">Address</p>
                      <p className="truncate text-sm font-bold">{c.address ?? '—'}</p>
                    </div>
                  </div>
                  <p className="mb-2 text-xs font-semibold text-ink-soft">Timeline</p>
                  {!timeline?.length && <p className="text-xs text-muted">No events yet.</p>}
                  <div className="space-y-2">
                    {timeline?.map((t) => (
                      <div key={t.id} className="flex items-center gap-3 rounded-2xl bg-card p-3">
                        <span className="h-2 w-2 shrink-0 rounded-full bg-leaf" />
                        <div className="min-w-0 flex-1">
                          <p className="text-sm font-semibold">{t.eventType}</p>
                          {t.notes && <p className="truncate text-xs text-muted">{t.notes}</p>}
                        </div>
                        <span className="text-xs text-muted">
                          {new Date(t.occurredAt).toLocaleString()}
                        </span>
                        <Chip tone="lavender">{t.source}</Chip>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>
          ))}
        </div>
      </Card>
    </div>
  )
}
