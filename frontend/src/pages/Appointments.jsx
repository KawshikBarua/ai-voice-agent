import { useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '../api/client'
import {
  Card, CardTitle, PageHeader, PillButton, Chip, Avatar, EmptyState, compactAction, statusTone,
} from '../components/ui'

// Compact enough to sit three-across in a row, but not so short that it is a poor target for a
// thumb: the extra height only applies below sm, where the pointer is a finger.
const rowAction = '!px-3 !py-2 !text-xs sm:!py-1.5'

function NewAppointmentForm({ onDone }) {
  const qc = useQueryClient()
  const { data: customers } = useQuery({
    queryKey: ['customers', 'all'],
    queryFn: () => api.get('/customers', { params: { pageSize: 100 } }).then(unwrap),
  })
  const { data: services } = useQuery({
    queryKey: ['services'],
    queryFn: () => api.get('/services', { params: { pageSize: 100 } }).then(unwrap),
  })
  const { data: employees } = useQuery({
    queryKey: ['employees'],
    queryFn: () => api.get('/employees').then(unwrap),
  })
  // Only people who are actually taking bookings can be chosen; the rest are still on the roster
  // for their existing appointments.
  const bookable = (employees ?? []).filter((e) => e.isActive)

  const [form, setForm] = useState({ customerId: '', serviceId: '', employeeId: '', date: '', time: '09:00', notes: '' })
  const [error, setError] = useState('')

  const create = useMutation({
    mutationFn: (body) => api.post('/appointments', body).then(unwrap),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['appointments'] })
      qc.invalidateQueries({ queryKey: ['dashboard'] })
      onDone()
    },
    onError: (err) => setError(err.response?.data?.message ?? 'Failed to create appointment.'),
  })

  const submit = (e) => {
    e.preventDefault()
    const service = services?.items?.find((s) => s.id === Number(form.serviceId))
    const startAt = new Date(`${form.date}T${form.time}`)
    const endAt = new Date(startAt.getTime() + (service?.durationMinutes ?? 30) * 60000)
    create.mutate({
      customerId: Number(form.customerId),
      serviceId: form.serviceId ? Number(form.serviceId) : null,
      // Left blank, the server hands it to whoever is on duty and free — the same choice the AI
      // makes on a call.
      employeeId: form.employeeId ? Number(form.employeeId) : null,
      startAt: startAt.toISOString(),
      endAt: endAt.toISOString(),
      amount: service?.minPrice ?? 0,
      notes: form.notes || null,
      status: 'Scheduled',
      paymentStatus: 'Unpaid',
    })
  }

  // min-w-0: date and time inputs have an intrinsic width that, as grid children, would push the
  // page wider than a small phone rather than shrinking to fit it.
  const input = 'w-full min-w-0 rounded-2xl border border-line bg-card px-4 py-2.5 text-base outline-none focus:border-ink'

  return (
    <form onSubmit={submit} className="grid gap-3 sm:grid-cols-2">
      <select required className={input} value={form.customerId}
        onChange={(e) => setForm({ ...form, customerId: e.target.value })}>
        <option value="">Select customer…</option>
        {customers?.items?.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
      </select>
      <select className={input} value={form.serviceId}
        onChange={(e) => setForm({ ...form, serviceId: e.target.value })}>
        <option value="">Select service…</option>
        {services?.items?.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
      </select>
      {bookable.length > 0 && (
        <select className={`${input} sm:col-span-2`} value={form.employeeId}
          onChange={(e) => setForm({ ...form, employeeId: e.target.value })}>
          <option value="">Anyone who is free…</option>
          {bookable.map((e) => (
            <option key={e.id} value={e.id}>{e.name}{e.jobTitle ? ` — ${e.jobTitle}` : ''}</option>
          ))}
        </select>
      )}
      <input required type="date" className={input} value={form.date}
        onChange={(e) => setForm({ ...form, date: e.target.value })} />
      <input required type="time" className={input} value={form.time}
        onChange={(e) => setForm({ ...form, time: e.target.value })} />
      <input placeholder="Notes (optional)" className={`${input} sm:col-span-2`} value={form.notes}
        onChange={(e) => setForm({ ...form, notes: e.target.value })} />
      {error && <p className="text-xs font-medium text-danger sm:col-span-2">{error}</p>}
      <div className="flex flex-col gap-2 sm:flex-row sm:col-span-2">
        <PillButton type="submit" className="w-full sm:w-auto" disabled={create.isPending}>
          {create.isPending ? 'Saving…' : 'Book appointment'}
        </PillButton>
        <PillButton type="button" variant="light" className="w-full sm:w-auto" onClick={onDone}>
          Cancel
        </PillButton>
      </div>
    </form>
  )
}

export default function Appointments() {
  const qc = useQueryClient()
  const [showForm, setShowForm] = useState(false)
  const [params] = useSearchParams()
  // Seeded from the dashboard stat tiles (/appointments?status=…)
  const [status, setStatus] = useState(() => params.get('status') ?? '')

  const { data, isLoading } = useQuery({
    queryKey: ['appointments', status],
    queryFn: () => api.get('/appointments', { params: { pageSize: 50, status: status || undefined } }).then(unwrap),
  })

  const [actionError, setActionError] = useState('')
  const [busyId, setBusyId] = useState(null)

  // Previously failures (e.g. 403) were swallowed, so buttons looked dead.
  const action = async (id, verb) => {
    setActionError('')
    setBusyId(`${id}-${verb}`)
    try {
      await api.post(`/appointments/${id}/${verb}`)
      qc.invalidateQueries({ queryKey: ['appointments'] })
      qc.invalidateQueries({ queryKey: ['dashboard'] })
      qc.invalidateQueries({ queryKey: ['sidebar-counts'] })
    } catch (err) {
      setActionError(
        err.response?.data?.message ??
        (err.response?.status === 403
          ? 'Your role does not permit this action.'
          : 'Action failed — please try again.'),
      )
    } finally {
      setBusyId(null)
    }
  }

  const fmt = (iso) =>
    new Date(iso).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })

  return (
    <div className="mx-auto max-w-5xl">
      <PageHeader
        title="Appointments"
        action={
          <PillButton className={compactAction} onClick={() => setShowForm((v) => !v)}>
            <span className="sm:hidden">+ New</span>
            <span className="hidden sm:inline">+ New Appointment</span>
          </PillButton>
        }
      />

      {showForm && (
        <Card className="mb-5">
          <CardTitle title="New appointment" />
          <NewAppointmentForm onDone={() => setShowForm(false)} />
        </Card>
      )}

      <Card>
      {/*
        Wrapped, not swipeable. A filter you cannot see is a filter nobody uses, and there is
        nothing on screen to say the row scrolls — so "Cancelled" would simply be missing on a
        phone. Two short lines of pills costs a few pixels of height and hides nothing.
      */}
      <div className="mb-4 flex flex-wrap gap-2">
          {['', 'Scheduled', 'Confirmed', 'Completed', 'Cancelled'].map((sVal) => (
            <button key={sVal} onClick={() => setStatus(sVal)}
              className={`shrink-0 rounded-pill px-3.5 py-2 text-xs font-semibold transition sm:py-1.5 ${
                status === sVal ? 'bg-ink text-on-ink' : 'bg-panel text-ink-soft hover:bg-line'
              }`}>
              {sVal || 'All'}
            </button>
          ))}
        </div>

        {actionError && (
          <p className="mb-3 rounded-2xl bg-danger-soft px-4 py-2.5 text-sm font-medium text-danger">
            {actionError}
          </p>
        )}

        {isLoading && <EmptyState message="Loading appointments…" />}
        {!isLoading && !data?.items?.length && <EmptyState message="No appointments found." />}

        <div className="space-y-3">
          {data?.items?.map((a, i) => (
            /*
              Three bands — who and when, the state chips, the actions. On a phone they stack in
              that order, which is also the order they are read in; from sm the same three sit on
              one line. Previously everything was one wrapping row, so on a narrow screen the
              chips and buttons broke wherever they happened to land and no two cards agreed.
            */
            <div key={a.id}
              className="flex flex-col gap-3 rounded-2xl bg-lavender/60 p-3.5 sm:flex-row sm:items-center sm:gap-4 sm:p-4">
              <div className="flex min-w-0 items-start gap-3 sm:flex-1 sm:items-center">
                <Avatar name={a.customerName ?? '?'} tone={['lavender', 'mint', 'cream'][i % 3]} />
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-bold sm:text-base">{a.serviceName ?? 'Appointment'}</p>
                  {/* The time carries the weight: it is what someone scans a list of bookings for. */}
                  <p className="mt-0.5 truncate text-xs text-ink-soft">
                    <span className="font-semibold text-ink">{fmt(a.startAt)}</span>
                    <span className="px-1.5 text-muted">·</span>
                    {a.customerName}
                  </p>
                  <p className="truncate text-xs text-muted">
                    {a.customerPhone}
                    {a.employeeName && <> · with <span className="font-semibold">{a.employeeName}</span></>}
                  </p>
                  {/* Truncated rather than wrapped: an address or a call note is context, and
                      letting either run to three lines turned one booking into half a screen. */}
                  {a.serviceAddress && <p className="mt-0.5 truncate text-xs text-muted">📍 {a.serviceAddress}</p>}
                  {a.notes && <p className="truncate text-xs text-muted">{a.notes}</p>}
                </div>
              </div>

              {/*
                On a phone the state chips and the actions share one line at the foot of the card,
                which is what stops a list of five bookings running past three screens. That line
                wraps, so a booking carrying three actions drops them to a second line instead of
                running off the side of the card. From sm the wrapper dissolves (`contents`) and
                both are columns of the row again, as before.
              */}
              <div className="flex flex-wrap items-center justify-between gap-2 sm:contents">
                <div className="flex min-w-0 flex-wrap items-center gap-1.5 sm:gap-2">
                  {a.isEmergency && <Chip tone="red">Emergency</Chip>}
                  {/* An address the AI took outside the coverage area: booked, but nobody has
                      promised the caller it is going ahead. Someone has to ring back. */}
                  {a.areaStatus === 'OutOfArea' && <Chip tone="cream">Confirm area</Chip>}
                  {/* The map could not be reached, so nothing was checked. Rare, and worth
                      knowing: the coverage rule quietly did not apply to this one. */}
                  {a.areaStatus === 'Unverified' && <Chip tone="cream">Address unchecked</Chip>}
                  <Chip tone={statusTone(a.status)}>{a.status}</Chip>
                  <Chip tone={statusTone(a.paymentStatus)}>{a.paymentStatus}</Chip>
                </div>

                {/* shrink-0 only from sm. On a phone this group has to be able to give way: held
                    at its max-content width it never wraps, and a Confirmed booking — mark paid,
                    complete, cancel — pushed its last button off the side of the screen. `grow`
                    keeps the buttons against the right edge whether they sit beside the chips or
                    on the line below them; it does not affect where the line breaks. */}
                <div className="flex grow flex-wrap justify-end gap-2 sm:grow-0 sm:shrink-0">
                  {a.status === 'Scheduled' && (
                    <PillButton variant="outline" className={rowAction}
                      disabled={busyId === `${a.id}-confirm`} onClick={() => action(a.id, 'confirm')}>
                      {busyId === `${a.id}-confirm` ? '…' : 'Confirm'}
                    </PillButton>
                  )}
                  {/* Payment can only be taken once the booking is confirmed */}
                  {(a.status === 'Confirmed' || a.status === 'Completed') && a.paymentStatus !== 'Paid' && (
                    <PillButton variant="outline" className={rowAction}
                      disabled={busyId === `${a.id}-mark-paid`} onClick={() => action(a.id, 'mark-paid')}>
                      {busyId === `${a.id}-mark-paid` ? '…' : 'Mark paid'}
                    </PillButton>
                  )}
                  {a.status === 'Confirmed' && (
                    <PillButton variant="outline" className={rowAction}
                      disabled={busyId === `${a.id}-complete`} onClick={() => action(a.id, 'complete')}>
                      {busyId === `${a.id}-complete` ? '…' : 'Complete'}
                    </PillButton>
                  )}
                  {(a.status === 'Scheduled' || a.status === 'Confirmed') && (
                    <PillButton variant="light" className={rowAction}
                      disabled={busyId === `${a.id}-cancel`} onClick={() => action(a.id, 'cancel')}>
                      {busyId === `${a.id}-cancel` ? '…' : 'Cancel'}
                    </PillButton>
                  )}
                </div>
              </div>
            </div>
          ))}
        </div>
      </Card>
    </div>
  )
}
