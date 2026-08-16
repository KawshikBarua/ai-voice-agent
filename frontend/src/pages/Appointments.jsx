import { useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '../api/client'
import { Card, CardTitle, PillButton, Chip, Avatar, EmptyState, statusTone } from '../components/ui'

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

  const [form, setForm] = useState({ customerId: '', serviceId: '', date: '', time: '09:00', notes: '' })
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
      startAt: startAt.toISOString(),
      endAt: endAt.toISOString(),
      amount: service?.minPrice ?? 0,
      notes: form.notes || null,
      status: 'Scheduled',
      paymentStatus: 'Unpaid',
    })
  }

  const input = 'w-full rounded-2xl border border-line bg-card px-4 py-2.5 text-[13.5px] outline-none focus:border-ink'

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
      <input required type="date" className={input} value={form.date}
        onChange={(e) => setForm({ ...form, date: e.target.value })} />
      <input required type="time" className={input} value={form.time}
        onChange={(e) => setForm({ ...form, time: e.target.value })} />
      <input placeholder="Notes (optional)" className={`${input} sm:col-span-2`} value={form.notes}
        onChange={(e) => setForm({ ...form, notes: e.target.value })} />
      {error && <p className="text-[12px] font-medium text-danger sm:col-span-2">{error}</p>}
      <div className="flex gap-2 sm:col-span-2">
        <PillButton type="submit" disabled={create.isPending}>
          {create.isPending ? 'Saving…' : 'Book appointment'}
        </PillButton>
        <PillButton type="button" variant="light" onClick={onDone}>Cancel</PillButton>
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
      <div className="mb-6 flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-[26px] font-bold">Appointments</h1>
        <PillButton onClick={() => setShowForm((v) => !v)}>+ New Appointment</PillButton>
      </div>

      {showForm && (
        <Card className="mb-5">
          <CardTitle title="New appointment" />
          <NewAppointmentForm onDone={() => setShowForm(false)} />
        </Card>
      )}

      <Card>
        <div className="mb-4 flex flex-wrap gap-2">
          {['', 'Scheduled', 'Confirmed', 'Completed', 'Cancelled'].map((sVal) => (
            <button key={sVal} onClick={() => setStatus(sVal)}
              className={`rounded-pill px-3.5 py-1.5 text-[12px] font-semibold transition ${
                status === sVal ? 'bg-ink text-on-ink' : 'bg-panel text-ink-soft hover:bg-line'
              }`}>
              {sVal || 'All'}
            </button>
          ))}
        </div>

        {actionError && (
          <p className="mb-3 rounded-2xl bg-danger-soft px-4 py-2.5 text-[12.5px] font-medium text-danger">
            {actionError}
          </p>
        )}

        {isLoading && <EmptyState message="Loading appointments…" />}
        {!isLoading && !data?.items?.length && <EmptyState message="No appointments found." />}

        <div className="space-y-3">
          {data?.items?.map((a, i) => (
            <div key={a.id} className="flex flex-wrap items-center gap-4 rounded-2xl bg-lavender/60 p-4">
              <Avatar name={a.customerName ?? '?'} tone={['lavender', 'mint', 'cream'][i % 3]} />
              <div className="min-w-0 flex-1">
                <p className="text-[14px] font-bold">{a.serviceName ?? 'Appointment'}</p>
                <p className="text-[12px] text-ink-soft">{fmt(a.startAt)} · {a.customerName} · {a.customerPhone}</p>
                {a.serviceAddress && <p className="text-[11.5px] text-muted">📍 {a.serviceAddress}</p>}
                {a.notes && <p className="text-[11.5px] text-muted">{a.notes}</p>}
              </div>
              {a.isEmergency && <Chip tone="red">Emergency</Chip>}
              <Chip tone={statusTone(a.status)}>{a.status}</Chip>
              <Chip tone={statusTone(a.paymentStatus)}>{a.paymentStatus}</Chip>
              <div className="flex flex-wrap gap-2">
                {a.status === 'Scheduled' && (
                  <PillButton variant="outline" className="!px-3 !py-1.5 !text-[11.5px]"
                    disabled={busyId === `${a.id}-confirm`} onClick={() => action(a.id, 'confirm')}>
                    {busyId === `${a.id}-confirm` ? '…' : 'Confirm'}
                  </PillButton>
                )}
                {/* Payment can only be taken once the booking is confirmed */}
                {(a.status === 'Confirmed' || a.status === 'Completed') && a.paymentStatus !== 'Paid' && (
                  <PillButton variant="outline" className="!px-3 !py-1.5 !text-[11.5px]"
                    disabled={busyId === `${a.id}-mark-paid`} onClick={() => action(a.id, 'mark-paid')}>
                    {busyId === `${a.id}-mark-paid` ? '…' : 'Mark paid'}
                  </PillButton>
                )}
                {a.status === 'Confirmed' && (
                  <PillButton variant="outline" className="!px-3 !py-1.5 !text-[11.5px]"
                    disabled={busyId === `${a.id}-complete`} onClick={() => action(a.id, 'complete')}>
                    {busyId === `${a.id}-complete` ? '…' : 'Complete'}
                  </PillButton>
                )}
                {(a.status === 'Scheduled' || a.status === 'Confirmed') && (
                  <PillButton variant="light" className="!px-3 !py-1.5 !text-[11.5px]"
                    disabled={busyId === `${a.id}-cancel`} onClick={() => action(a.id, 'cancel')}>
                    {busyId === `${a.id}-cancel` ? '…' : 'Cancel'}
                  </PillButton>
                )}
              </div>
            </div>
          ))}
        </div>
      </Card>
    </div>
  )
}
