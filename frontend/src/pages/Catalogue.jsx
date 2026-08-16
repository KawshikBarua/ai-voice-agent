import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '../api/client'
import { useAuthStore } from '../store/auth'
import { Card, CardTitle, PillButton, Chip, EmptyState } from '../components/ui'

const input = 'w-full rounded-2xl border border-line bg-card px-4 py-2.5 text-[13.5px] outline-none focus:border-ink'
const label = 'mb-1.5 block text-[12px] font-semibold text-ink-soft'

const BLANK_SERVICE = {
  name: '', description: '', durationMinutes: 30,
  minPrice: 0, maxPrice: 0, isEmergency: false, isAvailable: true,
}
const BLANK_PRODUCT = {
  name: '', sku: '', description: '', category: '',
  price: 0, quantity: 0, isAvailable: true, isActive: true,
}

/** Editing the catalogue re-syncs the agent, so changes reach callers automatically. */
function SyncNote() {
  return (
    <p className="mb-5 max-w-2xl text-[13px] text-ink-soft">
      These feed the “Services and pricing” and “Product catalogue” documents your AI reads on
      calls, and are the source of truth for the quote tools — so the AI never invents a price.
      Changes re-sync to Retell automatically.
    </p>
  )
}

function Field({ label: text, children, className = '' }) {
  return (
    <div className={className}>
      <label className={label}>{text}</label>
      {children}
    </div>
  )
}

function Toggle({ checked, onChange, children }) {
  return (
    <label className="flex items-center gap-2 text-[13px]">
      <input type="checkbox" checked={!!checked} onChange={(e) => onChange(e.target.checked)}
        className="h-4 w-4 rounded border-line" />
      {children}
    </label>
  )
}

function ServiceForm({ initial, onCancel, onSubmit, pending }) {
  const [form, setForm] = useState(initial)
  const set = (k) => (e) => setForm({ ...form, [k]: e.target.value })
  const setNum = (k) => (e) => setForm({ ...form, [k]: Number(e.target.value) || 0 })

  return (
    <form onSubmit={(e) => { e.preventDefault(); onSubmit(form) }} className="grid gap-3 sm:grid-cols-2">
      <Field label="Service name" className="sm:col-span-2">
        <input required className={input} value={form.name} onChange={set('name')}
          placeholder="e.g. Cleaning" />
      </Field>
      <Field label="Description" className="sm:col-span-2">
        <textarea rows={2} className={input} value={form.description ?? ''} onChange={set('description')}
          placeholder="What this service includes — the AI reads this to answer questions" />
      </Field>
      <Field label="Duration (minutes)">
        <input type="number" min="5" step="5" className={input}
          value={form.durationMinutes} onChange={setNum('durationMinutes')} />
      </Field>
      <Field label="Price from">
        <input type="number" min="0" step="0.01" className={input}
          value={form.minPrice} onChange={setNum('minPrice')} />
      </Field>
      <Field label="Price to">
        <input type="number" min="0" step="0.01" className={input}
          value={form.maxPrice} onChange={setNum('maxPrice')} />
      </Field>
      <div className="flex items-end gap-4">
        <Toggle checked={form.isAvailable} onChange={(v) => setForm({ ...form, isAvailable: v })}>
          Bookable
        </Toggle>
        <Toggle checked={form.isEmergency} onChange={(v) => setForm({ ...form, isEmergency: v })}>
          Emergency / same-day
        </Toggle>
      </div>
      <div className="flex gap-2 sm:col-span-2">
        <PillButton type="submit" disabled={pending}>{pending ? 'Saving…' : 'Save service'}</PillButton>
        <PillButton type="button" variant="light" onClick={onCancel}>Cancel</PillButton>
      </div>
    </form>
  )
}

function ProductForm({ initial, onCancel, onSubmit, pending }) {
  const [form, setForm] = useState(initial)
  const set = (k) => (e) => setForm({ ...form, [k]: e.target.value })
  const setNum = (k) => (e) => setForm({ ...form, [k]: Number(e.target.value) || 0 })

  return (
    <form onSubmit={(e) => { e.preventDefault(); onSubmit(form) }} className="grid gap-3 sm:grid-cols-2">
      <Field label="Product name">
        <input required className={input} value={form.name} onChange={set('name')}
          placeholder="e.g. Ibuprofen 200mg" />
      </Field>
      <Field label="SKU">
        <input className={input} value={form.sku ?? ''} onChange={set('sku')} placeholder="Optional" />
      </Field>
      <Field label="Category">
        <input className={input} value={form.category ?? ''} onChange={set('category')}
          placeholder="e.g. Medication" />
      </Field>
      <Field label="Price">
        <input type="number" min="0" step="0.01" className={input} value={form.price} onChange={setNum('price')} />
      </Field>
      <Field label="Quantity in stock">
        <input type="number" min="0" className={input} value={form.quantity} onChange={setNum('quantity')} />
      </Field>
      <div className="flex items-end gap-4">
        <Toggle checked={form.isAvailable} onChange={(v) => setForm({ ...form, isAvailable: v })}>
          In stock
        </Toggle>
        <Toggle checked={form.isActive} onChange={(v) => setForm({ ...form, isActive: v })}>
          Listed
        </Toggle>
      </div>
      <Field label="Description" className="sm:col-span-2">
        <textarea rows={2} className={input} value={form.description ?? ''} onChange={set('description')} />
      </Field>
      <div className="flex gap-2 sm:col-span-2">
        <PillButton type="submit" disabled={pending}>{pending ? 'Saving…' : 'Save product'}</PillButton>
        <PillButton type="button" variant="light" onClick={onCancel}>Cancel</PillButton>
      </div>
    </form>
  )
}

function Row({ title, meta, chips, onEdit, onDelete }) {
  return (
    <div className="flex items-start gap-4 rounded-2xl bg-panel p-4">
      <div className="min-w-0 flex-1">
        <div className="mb-1 flex flex-wrap items-center gap-2">
          <p className="text-[14px] font-bold">{title}</p>
          {chips}
        </div>
        <p className="text-[12.5px] text-ink-soft">{meta}</p>
      </div>
      <div className="flex shrink-0 gap-1">
        <button onClick={onEdit}
          className="rounded-pill px-3 py-1 text-[11.5px] font-semibold text-ink hover:bg-line">Edit</button>
        <button onClick={onDelete}
          className="rounded-pill px-3 py-1 text-[11.5px] font-semibold text-danger hover:bg-danger-soft">Delete</button>
      </div>
    </div>
  )
}

function useCrud(resource, queryKey) {
  const qc = useQueryClient()
  // The catalogue drives the generated knowledge documents, so refresh that preview too.
  const invalidate = () => {
    qc.invalidateQueries({ queryKey: [queryKey] })
    qc.invalidateQueries({ queryKey: ['final-prompt'] })
  }
  return {
    create: useMutation({ mutationFn: (b) => api.post(`/${resource}`, b).then(unwrap), onSuccess: invalidate }),
    update: useMutation({ mutationFn: ({ id, ...b }) => api.put(`/${resource}/${id}`, b).then(unwrap), onSuccess: invalidate }),
    remove: useMutation({ mutationFn: (id) => api.delete(`/${resource}/${id}`), onSuccess: invalidate }),
  }
}

function ServicesSection({ currency }) {
  const [editing, setEditing] = useState(null) // null | 'new' | service object
  const { data, isLoading, error } = useQuery({
    queryKey: ['services'],
    queryFn: () => api.get('/services').then(unwrap),
  })
  const { create, update, remove } = useCrud('services', 'services')

  const submit = (form) => {
    const done = () => setEditing(null)
    if (editing === 'new') create.mutate(form, { onSuccess: done })
    else update.mutate({ ...form, id: editing.id }, { onSuccess: done })
  }

  return (
    <Card className="mb-5">
      <CardTitle
        title="Services and pricing"
        subtitle="What customers can book, and what it costs"
        action={<PillButton onClick={() => setEditing(editing === 'new' ? null : 'new')}>
          {editing === 'new' ? 'Close' : '+ New Service'}
        </PillButton>}
      />

      {editing && (
        <div className="mb-4 rounded-2xl bg-panel p-4">
          <ServiceForm
            key={editing === 'new' ? 'new' : editing.id}
            initial={editing === 'new' ? BLANK_SERVICE : editing}
            onCancel={() => setEditing(null)}
            onSubmit={submit}
            pending={create.isPending || update.isPending}
          />
        </div>
      )}

      {isLoading && <EmptyState message="Loading services…" />}
      {error && <EmptyState message="Could not load services. Please retry." />}
      {!isLoading && !error && !data?.items?.length && (
        <EmptyState message="No services yet. Add what your business offers so the AI can book and quote it." />
      )}

      <div className="space-y-3">
        {data?.items?.map((s) => (
          <Row
            key={s.id}
            title={s.name}
            chips={<>
              {!s.isAvailable && <Chip tone="red">Not bookable</Chip>}
              {s.isEmergency && <Chip tone="cream">Emergency</Chip>}
            </>}
            meta={`${currency} ${s.minPrice} – ${currency} ${s.maxPrice} · ${s.durationMinutes} min` +
              (s.description ? ` · ${s.description}` : '')}
            onEdit={() => setEditing(s)}
            onDelete={() => remove.mutate(s.id)}
          />
        ))}
      </div>
    </Card>
  )
}

function ProductsSection({ currency }) {
  const [editing, setEditing] = useState(null)
  const { data, isLoading, error } = useQuery({
    queryKey: ['products'],
    queryFn: () => api.get('/products').then(unwrap),
  })
  const { create, update, remove } = useCrud('products', 'products')

  const submit = (form) => {
    const done = () => setEditing(null)
    if (editing === 'new') create.mutate(form, { onSuccess: done })
    else update.mutate({ ...form, id: editing.id }, { onSuccess: done })
  }

  return (
    <Card>
      <CardTitle
        title="Product catalogue"
        subtitle="Items you sell, with price and stock"
        action={<PillButton onClick={() => setEditing(editing === 'new' ? null : 'new')}>
          {editing === 'new' ? 'Close' : '+ New Product'}
        </PillButton>}
      />

      {editing && (
        <div className="mb-4 rounded-2xl bg-panel p-4">
          <ProductForm
            key={editing === 'new' ? 'new' : editing.id}
            initial={editing === 'new' ? BLANK_PRODUCT : editing}
            onCancel={() => setEditing(null)}
            onSubmit={submit}
            pending={create.isPending || update.isPending}
          />
        </div>
      )}

      {isLoading && <EmptyState message="Loading products…" />}
      {error && <EmptyState message="Could not load products. Please retry." />}
      {!isLoading && !error && !data?.items?.length && (
        <EmptyState message="No products yet. Add anything you sell so the AI can quote it accurately." />
      )}

      <div className="space-y-3">
        {data?.items?.map((p) => (
          <Row
            key={p.id}
            title={p.name}
            chips={<>
              {p.category && <Chip tone="lavender">{p.category}</Chip>}
              {!p.isActive && <Chip tone="red">Unlisted</Chip>}
              {p.isActive && (!p.isAvailable || p.quantity <= 0) && <Chip tone="cream">Out of stock</Chip>}
            </>}
            meta={`${currency} ${p.price} · ${p.quantity} in stock` +
              (p.sku ? ` · SKU ${p.sku}` : '') + (p.description ? ` · ${p.description}` : '')}
            onEdit={() => setEditing(p)}
            onDelete={() => remove.mutate(p.id)}
          />
        ))}
      </div>
    </Card>
  )
}

export default function Catalogue() {
  const role = useAuthStore((s) => s.user?.role)
  const canManage = ['SuperAdmin', 'OrgAdmin', 'Manager'].includes(role)

  const { data: org } = useQuery({
    queryKey: ['organization'],
    queryFn: () => api.get('/settings/organization').then(unwrap),
  })
  const currency = org?.currency ?? ''

  return (
    <div>
      <h1 className="mb-6 text-[26px] font-bold">Services &amp; Products</h1>
      <SyncNote />

      {!canManage && (
        <Card className="mb-5">
          <p className="text-[13px] text-ink-soft">
            You can view the catalogue, but only an administrator or manager can change it.
          </p>
        </Card>
      )}

      <ServicesSection currency={currency} />
      {org?.productsEnabled !== false && <ProductsSection currency={currency} />}
    </div>
  )
}
