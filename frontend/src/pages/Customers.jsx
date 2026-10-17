import { Fragment, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { keepPreviousData, useInfiniteQuery, useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { AnimatePresence, MotionConfig, motion } from 'framer-motion'
import { api, unwrap } from '../api/client'
import {
  Card, CardTitle, PageHeader, PillButton, Chip, Chevron, Avatar, EmptyState, compactAction,
  tableHead, tableCell, tableScroller,
} from '../components/ui'
import { DESKTOP, useDebounced, useLoadMore, useMediaQuery } from '../lib/hooks'
import { useBusinessTimeZone } from '../lib/businessTime'
import { formatInZone } from '../lib/format'

const PAGE_SIZE = 50
const TONES = ['lavender', 'mint', 'cream']

const fmtDate = (iso, timeZone) =>
  (iso ? formatInZone(iso, timeZone, { year: 'numeric', month: 'numeric', day: 'numeric' }) : '—')

function CustomerDetail({ customer: c, timeline }) {
  const timeZone = useBusinessTimeZone()
  return (
    <>
      <div className="mb-3 grid grid-cols-3 gap-3 text-center">
        <div className="rounded-2xl bg-card p-3">
          <p className="text-xs text-muted">First visit</p>
          <p className="text-sm font-bold">{fmtDate(c.firstVisit, timeZone)}</p>
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
              {formatInZone(t.occurredAt, timeZone)}
            </span>
            <Chip tone="lavender">{t.source}</Chip>
          </div>
        ))}
      </div>
    </>
  )
}


function CustomerTable({ customers, loading, expanded, onToggle, timeline }) {
  const timeZone = useBusinessTimeZone()
  return (
    <table className="w-full table-fixed border-separate border-spacing-0 text-left text-sm">
      <colgroup>
        <col />
        <col className="w-40" />
        <col className="w-[26%]" />
        <col className="w-20" />
        <col className="w-32" />
        <col className="w-12" />
      </colgroup>
      <thead className="sticky top-0 z-10 bg-panel/90 backdrop-blur">
        <tr>
          <th scope="col" className={tableHead}>Customer</th>
          <th scope="col" className={tableHead}>Phone</th>
          <th scope="col" className={tableHead}>Email</th>
          <th scope="col" className={`${tableHead} text-right`}>Visits</th>
          <th scope="col" className={tableHead}>Last visit</th>
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
            {[24, 36, 8, 16].map((w, j) => (
              <td key={j} className={tableCell}><span className="block h-3 rounded-full bg-panel" style={{ width: `${w * 4}px`, maxWidth: '100%' }} /></td>
            ))}
            <td className={tableCell} />
          </tr>
        ))}

        {customers.map((c, i) => {
          const open = expanded === c.id
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
                    <Avatar name={c.name} size="sm" tone={TONES[c.id % 3]} />
                    <span className="truncate font-semibold">{c.name}</span>
                    {c.totalVisits > 1 && <span className="shrink-0"><Chip tone="mint">Returning</Chip></span>}
                  </button>
                </td>
                <td className={`${tableCell} truncate tabular-nums text-ink-soft`}>{c.phone}</td>
                <td className={`${tableCell} truncate text-ink-soft`}>{c.email ?? '—'}</td>
                <td className={`${tableCell} text-right font-semibold tabular-nums`}>{c.totalVisits}</td>
                <td className={`${tableCell} text-ink-soft`}>{fmtDate(c.lastVisit, timeZone)}</td>
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
                          <CustomerDetail customer={c} timeline={timeline} />
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

function CustomerCards({ customers, expanded, onToggle, timeline }) {
  const timeZone = useBusinessTimeZone()
  return (
    <div className="space-y-3">
      {customers.map((c) => (
        <div key={c.id} className="rounded-2xl bg-panel p-4">
          <button
            className="flex w-full items-center gap-3 text-left sm:gap-4"
            aria-expanded={expanded === c.id}
            onClick={() => onToggle(c.id)}
          >
            <Avatar name={c.name} tone={TONES[c.id % 3]} />
            <div className="min-w-0 flex-1">
              {/* Both truncated: an email long enough to wrap took the contact line to two
                  rows and left the "Returning" chip floating beside a ragged block. */}
              <p className="truncate text-sm font-bold sm:text-base">{c.name}</p>
              <p className="truncate text-xs text-ink-soft">{c.phone}{c.email ? ` · ${c.email}` : ''}</p>
            </div>
            {c.totalVisits > 1 && <span className="shrink-0"><Chip tone="mint">Returning</Chip></span>}
            <div className="hidden text-right sm:block">
              <p className="text-xs text-muted">Last visit</p>
              <p className="text-sm font-semibold">{fmtDate(c.lastVisit, timeZone)}</p>
            </div>
          </button>

          {expanded === c.id && (
            <div className="mt-4 border-t border-line pt-4">
              <CustomerDetail customer={c} timeline={timeline} />
            </div>
          )}
        </div>
      ))}
    </div>
  )
}

export default function Customers() {
  const qc = useQueryClient()
  const [params] = useSearchParams()
  // Seeded from the dashboard search box (/customers?q=…)
  const [search, setSearch] = useState(() => params.get('q') ?? '')
  const [showForm, setShowForm] = useState(false)
  const [expanded, setExpanded] = useState(null)
  const [form, setForm] = useState({ name: '', phone: '', email: '', address: '' })

  const isDesktop = useMediaQuery(DESKTOP)
  const term = useDebounced(search.trim())

  // Pages are fetched by cursor as the list is scrolled, so the first screen costs the same
  // whether the business has fifty customers or a million.
  const list = useInfiniteQuery({
    queryKey: ['customers', 'list', term],
    // `signal` cancels a search that has been superseded before its answer arrives.
    queryFn: ({ pageParam, signal }) =>
      api.get('/customers', {
        params: { search: term || undefined, cursor: pageParam ?? undefined, pageSize: PAGE_SIZE },
        signal,
      }).then(unwrap),
    initialPageParam: null,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    // Keeps the previous results on screen while a new search loads, instead of blanking the list.
    placeholderData: keepPreviousData,
  })
  const { data, isLoading, isError, isPlaceholderData, isFetchingNextPage } = list
  const customers = data?.pages.flatMap((p) => p.items) ?? []

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

  const { scrollerRef, sentinelRef } = useLoadMore(list, { contained: isDesktop, count: customers.length })

  const toggle = (id) => setExpanded((cur) => (cur === id ? null : id))
  const input = 'w-full rounded-2xl border border-line bg-card px-4 py-2.5 text-base outline-none focus:border-ink'
  const empty = !isLoading && !isError && customers.length === 0

  return (
    <MotionConfig reducedMotion="user">
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
              maxLength={100}
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>

          {isError && <EmptyState message="Couldn't load customers. Please try again." />}
          {empty && <EmptyState message="No customers found." />}
          {isLoading && !isDesktop && <EmptyState message="Loading customers…" />}

          {/* On a desktop this box is the scroller: the header row sticks to its top and only
              the rows move. On a phone it has no height of its own and the page scrolls. */}
          <div
            ref={scrollerRef}
            className={`transition-opacity duration-200 ${isPlaceholderData ? 'opacity-60' : ''} ${
              isDesktop && !empty && !isError
                ? tableScroller
                : ''
            }`}
          >
            {isDesktop
              ? !empty && !isError && (
                <CustomerTable customers={customers} loading={isLoading}
                  expanded={expanded} onToggle={toggle} timeline={timeline} />
              )
              : <CustomerCards customers={customers} expanded={expanded} onToggle={toggle} timeline={timeline} />}

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
