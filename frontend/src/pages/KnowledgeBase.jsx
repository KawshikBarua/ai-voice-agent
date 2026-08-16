import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '../api/client'
import { Card, CardTitle, PillButton, Chip, EmptyState } from '../components/ui'

const CATEGORIES = ['BusinessInfo', 'FAQ', 'Policy', 'EmergencyRule', 'Custom']
const CATEGORY_TONES = { BusinessInfo: 'lavender', FAQ: 'mint', Policy: 'cream', EmergencyRule: 'red', Custom: 'dark' }

export default function KnowledgeBase() {
  const qc = useQueryClient()
  const [showForm, setShowForm] = useState(false)
  const [showPrompt, setShowPrompt] = useState(false)
  const [form, setForm] = useState({ category: 'FAQ', title: '', content: '' })

  const { data: entries, isLoading } = useQuery({
    queryKey: ['knowledge-base'],
    queryFn: () => api.get('/knowledge-base').then(unwrap),
  })

  const { data: finalPrompt } = useQuery({
    queryKey: ['final-prompt'],
    enabled: showPrompt,
    queryFn: () => api.get('/knowledge-base/final-prompt').then(unwrap),
  })

  const create = useMutation({
    mutationFn: (body) => api.post('/knowledge-base', body).then(unwrap),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['knowledge-base'] })
      qc.invalidateQueries({ queryKey: ['final-prompt'] })
      setShowForm(false)
      setForm({ category: 'FAQ', title: '', content: '' })
    },
  })

  const remove = useMutation({
    mutationFn: (id) => api.delete(`/knowledge-base/${id}`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['knowledge-base'] })
      qc.invalidateQueries({ queryKey: ['final-prompt'] })
    },
  })

  const input = 'w-full rounded-2xl border border-line bg-card px-4 py-2.5 text-[13.5px] outline-none focus:border-ink'

  return (
    <div>
      <div className="mb-6 flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-[26px] font-bold">Knowledge Base</h1>
        <div className="flex gap-2">
          <PillButton variant="outline" onClick={() => setShowPrompt((v) => !v)}>
            {showPrompt ? 'Hide final prompt' : 'Preview final AI prompt'}
          </PillButton>
          <PillButton onClick={() => setShowForm((v) => !v)}>+ New Entry</PillButton>
        </div>
      </div>

      <p className="mb-5 max-w-2xl text-[13px] text-ink-soft">
        Frontly answers using this knowledge. The core AI instructions are managed by the
        platform and cannot be edited — only your business-specific entries below are used to build the final prompt.
      </p>

      {showPrompt && (
        <Card className="mb-5">
          <CardTitle
            title="What the AI receives"
            subtitle="Generated automatically — read only"
            action={
              finalPrompt && (
                <div className="flex flex-wrap gap-2">
                  <Chip tone="lavender">Prompt {finalPrompt.promptCharacters.toLocaleString()} chars</Chip>
                  <Chip tone="mint">Knowledge {finalPrompt.knowledgeCharacters.toLocaleString()} chars</Chip>
                </div>
              )
            }
          />
          <p className="mb-3 text-[12px] text-muted">
            Rules, conversation flow and business details stay in the <strong>system prompt</strong> (sent on
            every turn). Catalogues, FAQs and policies go to Retell's <strong>knowledge base</strong>, which the
            AI searches only when it needs them — keeping calls fast and cheap.
          </p>

          <p className="mb-1.5 text-[12px] font-semibold text-ink-soft">System prompt</p>
          <pre className="mb-4 max-h-80 overflow-auto whitespace-pre-wrap rounded-2xl bg-panel p-4 text-[12px] leading-relaxed text-ink-soft">
            {finalPrompt?.prompt ?? 'Loading…'}
          </pre>

          <p className="mb-1.5 text-[12px] font-semibold text-ink-soft">
            Knowledge base documents ({finalPrompt?.knowledgeDocuments?.length ?? 0})
          </p>
          <div className="space-y-2">
            {finalPrompt?.knowledgeDocuments?.map((d) => (
              <details key={d.title} className="rounded-2xl bg-panel p-3.5">
                <summary className="cursor-pointer text-[13px] font-semibold">
                  {d.title} <span className="font-normal text-muted">· {d.characters.toLocaleString()} chars</span>
                </summary>
                <pre className="mt-2 max-h-64 overflow-auto whitespace-pre-wrap text-[12px] leading-relaxed text-ink-soft">
                  {d.text}
                </pre>
              </details>
            ))}
          </div>
        </Card>
      )}

      {showForm && (
        <Card className="mb-5">
          <CardTitle title="New knowledge entry" />
          <form
            onSubmit={(e) => { e.preventDefault(); create.mutate({ ...form, sortOrder: (entries?.length ?? 0) + 1 }) }}
            className="grid gap-3"
          >
            <select className={input} value={form.category}
              onChange={(e) => setForm({ ...form, category: e.target.value })}>
              {CATEGORIES.map((c) => <option key={c}>{c}</option>)}
            </select>
            <input required placeholder="Title (e.g. 'Do you accept walk-ins?')" className={input} value={form.title}
              onChange={(e) => setForm({ ...form, title: e.target.value })} />
            <textarea required rows={4} placeholder="Content the AI should know…" className={input} value={form.content}
              onChange={(e) => setForm({ ...form, content: e.target.value })} />
            <div className="flex gap-2">
              <PillButton type="submit" disabled={create.isPending}>Save entry</PillButton>
              <PillButton type="button" variant="light" onClick={() => setShowForm(false)}>Cancel</PillButton>
            </div>
          </form>
        </Card>
      )}

      <Card>
        {isLoading && <EmptyState message="Loading knowledge base…" />}
        {!isLoading && !entries?.length && <EmptyState message="No entries yet. Add FAQs, policies and business info for your AI." />}
        <div className="space-y-3">
          {entries?.map((e) => (
            <div key={e.id} className="flex items-start gap-4 rounded-2xl bg-panel p-4">
              <div className="min-w-0 flex-1">
                <div className="mb-1 flex items-center gap-2">
                  <Chip tone={CATEGORY_TONES[e.category] ?? 'lavender'}>{e.category}</Chip>
                  <p className="text-[14px] font-bold">{e.title}</p>
                </div>
                <p className="text-[12.5px] text-ink-soft">{e.content}</p>
              </div>
              <button
                onClick={() => remove.mutate(e.id)}
                className="rounded-pill px-3 py-1 text-[11.5px] font-semibold text-danger hover:bg-danger-soft"
              >
                Delete
              </button>
            </div>
          ))}
        </div>
      </Card>
    </div>
  )
}
