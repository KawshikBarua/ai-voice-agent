import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { api, unwrap } from '../api/client'
import { useAuthStore } from '../store/auth'
import { Card, CardTitle, PillButton, Chip, EmptyState } from '../components/ui'
import { GaugeMeter, BarMeter } from '../components/charts'
import { currencySymbol, minutes as fmtMinutes } from '../lib/format'

/**
 * What this account is being charged, and why.
 *
 * The page answers three questions in order: what is the bill shaping up to be, what did I use to
 * get there, and what have I been charged before. Every amount is shown with the arithmetic that
 * produced it — an unexplained line on a bill is the thing customers write in about.
 */

/** Money on this page is exact to the penny: it is what someone is being charged, so the
 *  dashboard's rounded "1.2K" style would be wrong here. */
const exact = (n, code = 'USD') =>
  `${currencySymbol(code)}${Number(n ?? 0).toLocaleString(undefined, {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  })}`

const day = (d) =>
  d ? new Date(d).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' }) : '—'

const dayShort = (d) =>
  d ? new Date(d).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) : '—'

const INVOICE_TONE = {
  paid: 'mint',
  open: 'cream',
  draft: 'lavender',
  uncollectible: 'red',
  void: 'lavender',
}

function useBilling() {
  return useQuery({
    queryKey: ['billing-summary'],
    queryFn: () => api.get('/billing/summary').then(unwrap),
  })
}

/** Every tier on offer. Each carries `canPayOnline`, which is false when the platform has no
 *  Stripe connection or the tier has no Stripe price — the tier is still shown either way. */
function usePlans() {
  return useQuery({
    queryKey: ['billing-plans'],
    queryFn: () => api.get('/billing/plans').then(unwrap),
  })
}

const cyclePer = (cycle) => (String(cycle).toLowerCase() === 'yearly' ? 'year' : 'month')

/** What a tier costs and what it includes. Read the same whether it can be picked or not, because
 *  it is the same offer either way — only how it gets paid for differs. */
function PlanFacts({ plan, isCurrent }) {
  return (
    <>
      <span className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
        <span className="text-[14px] font-bold">{plan.name}</span>
        {isCurrent && <Chip tone="mint">Your plan</Chip>}
      </span>
      <span className="mt-1 block text-[18px] font-bold tabular-nums">
        {exact(plan.amount, plan.currency)}
        <span className="ml-1 text-[12px] font-medium text-muted">per {cyclePer(plan.billingCycle)}</span>
      </span>
      {plan.description && (
        <span className="mt-1 block text-[12.5px] leading-snug text-ink-soft">{plan.description}</span>
      )}
      <span className="mt-1.5 block text-[12px] leading-relaxed text-muted">
        {plan.includedMinutes > 0
          ? `${fmtMinutes(plan.includedMinutes)} of talk time included`
          : 'Talk time is not metered'}
        {plan.includedMinutes > 0 && (
          <>
            {' · '}
            {plan.overageRatePerMinute > 0
              ? `${plan.overageRatePerMinute} ${plan.currency} for each minute past that`
              : 'extra minutes are not charged'}
          </>
        )}
      </span>
    </>
  )
}

/** A tier that can be paid for online: pick it, then pay. */
function PlanOption({ plan, selected, isCurrent, onSelect }) {
  return (
    <button
      role="radio"
      aria-checked={selected}
      onClick={() => onSelect(plan.id)}
      className={`rounded-2xl border p-4 text-left transition ${
        selected ? 'border-ink bg-panel' : 'border-line bg-card hover:bg-panel'
      }`}
    >
      <PlanFacts plan={plan} isCurrent={isCurrent} />
    </button>
  )
}

/** A tier this viewer cannot pay for from here — either Stripe cannot collect for it, or they do
 *  not hold a role that may commit the account to a charge. Shown as information rather than a
 *  control that would go nowhere, with the reason said plainly. */
function PlanCard({ plan, isCurrent, note }) {
  return (
    <div className="rounded-2xl border border-line bg-card p-4">
      <PlanFacts plan={plan} isCurrent={isCurrent} />
      {note && <span className="mt-2 block text-[11.5px] font-medium text-muted">{note}</span>}
    </div>
  )
}

/**
 * The tiers, and the one action that acts on them.
 *
 * The list is shown whatever the state of the platform's Stripe connection: a customer deciding
 * what to be on needs to see what is on offer, and a tier that Stripe cannot collect for is still
 * a tier their provider can put them on. Only the pay button is conditional.
 */
function PlanChooser({
  currentPlanId, canSubscribe, canChangePlan, pendingPlanId, pendingPlanName, periodEnd,
  busy, onCheckout, onSwitch,
}) {
  const { data: plans, isLoading, isError } = usePlans()
  const [chosen, setChosen] = useState(null)
  // Mirrors the roles the billing endpoints accept. Everyone can read what the account is on;
  // only the people who may commit it to a charge get the control.
  const role = useAuthStore((s) => s.user?.role)
  const mayPay = role === 'OrgAdmin' || role === 'Manager'
  // Switching applies from the next cycle, so it is a different promise from paying today.
  const switching = canChangePlan && mayPay

  if (isLoading) {
    return (
      <Card>
        <CardTitle title="Plans" />
        <EmptyState message="Loading the plans…" />
      </Card>
    )
  }

  if (isError || !plans?.length) {
    return (
      <Card>
        <CardTitle title="Plans" />
        <EmptyState message="No plans have been published yet. Your provider will arrange one with you directly." />
      </Card>
    )
  }

  // Only tiers Stripe can take money for are ever selectable. Subscribing offers all of them.
  // Switching drops the tier already in force — it is not somewhere to move to — unless a move is
  // queued, in which case picking it back is how a customer who has changed their mind calls the
  // move off, so it returns to the list.
  const queued = Boolean(pendingPlanId)
  const online = plans.filter((p) => p.canPayOnline)
  const payable =
    canSubscribe && mayPay ? online
      : switching ? online.filter((p) => p.id !== currentPlanId || queued)
        : []
  const selectable = payable.length > 0

  // Subscribing defaults to the tier already recorded, so the common case is one click. Switching
  // has no such default: it opens on the queued tier if there is one, else the first alternative.
  const preferred = queued ? pendingPlanId : currentPlanId
  const selected =
    chosen ?? (payable.some((p) => p.id === preferred) ? preferred : payable[0]?.id)

  // Picking the tier in force while a move is queued means "call it off", not "move".
  const cancelling = switching && queued && selected === currentPlanId

  const title = switching ? 'Your plan' : selectable ? 'Choose your plan' : 'Plans'
  const subtitle =
    switching
      ? 'Moving to another plan takes effect at your next billing cycle. Nothing is charged today.'
      : selectable
        ? "Pick the one that suits you. You pay on Stripe's page — your card never reaches us."
        : canSubscribe && !mayPay
          ? 'What is on offer. An owner or manager on your account handles payment.'
          : 'What is on offer. Your provider arranges and invoices these directly.'

  return (
    <Card>
      <CardTitle title={title} subtitle={subtitle} />

      {/* A move already agreed. Said before the list, so the tiers below are read in its light. */}
      {pendingPlanName && (
        <div className="mb-3 rounded-2xl bg-mint px-3 py-2 text-[12.5px] leading-snug">
          You are moving to <strong>{pendingPlanName}</strong>
          {periodEnd ? ` on ${day(periodEnd)}` : ' at your next billing cycle'}. Until then you keep
          your current plan and its included minutes.
        </div>
      )}
      <div
        role={selectable ? 'radiogroup' : undefined}
        aria-label={selectable ? 'Plan' : undefined}
        className="grid gap-3 @3xl:grid-cols-2"
      >
        {plans.map((p) =>
          payable.some((q) => q.id === p.id) ? (
            <PlanOption
              key={p.id}
              plan={p}
              selected={selected === p.id}
              isCurrent={p.id === currentPlanId}
              onSelect={setChosen}
            />
          ) : (
            <PlanCard
              key={p.id}
              plan={p}
              isCurrent={p.id === currentPlanId}
              // Silent when the tier is fine and only this viewer's role holds them back — the
              // subtitle has already said so once, and repeating it on every tier is nagging.
              note={p.canPayOnline ? null : 'Arranged with your provider — not payable online'}
            />
          ),
        )}
      </div>
      {selectable && (
        <div className="mt-4 flex justify-end">
          <PillButton disabled={busy} onClick={() => (switching ? onSwitch : onCheckout)(selected)}>
            {busy
              ? switching ? 'Changing…' : 'Opening…'
              : cancelling ? 'Cancel the change'
                : switching ? 'Switch from next cycle' : 'Continue to payment'}
          </PillButton>
        </div>
      )}
    </Card>
  )
}

/** A single line of the next bill, with the reason underneath it. */
function ChargeRow({ line, currency }) {
  return (
    <div className="flex items-start justify-between gap-4 border-b border-line/60 py-3 last:border-0">
      <div className="min-w-0">
        <p className="text-[13.5px] font-semibold">
          {line.label}
          {!line.isFinal && (
            <span className="ml-2 align-middle text-[10.5px] font-medium uppercase tracking-wide text-muted">
              so far
            </span>
          )}
        </p>
        {line.detail && <p className="mt-0.5 text-[12px] leading-snug text-muted">{line.detail}</p>}
      </div>
      <p className="shrink-0 text-[14px] font-bold tabular-nums">{exact(line.amount, currency)}</p>
    </div>
  )
}

/** One closed period, spelled out the same way whether it cost extra or not. */
function PeriodRow({ p }) {
  const over = p.overageMinutes > 0
  return (
    <div className="border-b border-line/60 py-3 last:border-0">
      <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
        <p className="text-[13.5px] font-semibold">
          {dayShort(p.periodStart)} – {day(p.periodEnd)}
        </p>
        <p className="text-[14px] font-bold tabular-nums">{exact(p.total, p.currency)}</p>
      </div>
      <p className="mt-1 text-[12px] leading-relaxed text-muted">
        {p.planName} plan {exact(p.baseAmount, p.currency)}
        {p.includedMinutes > 0 ? (
          <>
            {' · '}
            {fmtMinutes(p.minutesUsed)} used of {fmtMinutes(p.includedMinutes)} included
          </>
        ) : (
          <>
            {' · '}
            {fmtMinutes(p.minutesUsed)} used (unmetered)
          </>
        )}
        {over && (
          <>
            {' · '}
            <span className="font-semibold text-[var(--color-crit)]">
              {p.overageMinutes.toLocaleString()} min over
            </span>
            {p.overageAmount > 0
              ? ` at ${p.overageRatePerMinute} ${p.currency}/min = ${exact(p.overageAmount, p.currency)}`
              : ' — not charged'}
          </>
        )}
      </p>
    </div>
  )
}

function InvoiceRow({ inv }) {
  const [open, setOpen] = useState(false)
  const tone = INVOICE_TONE[String(inv.status).toLowerCase()] ?? 'lavender'

  return (
    <div className="border-b border-line/60 py-3 last:border-0">
      <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-2">
        <div className="min-w-0">
          <p className="flex items-center gap-2 text-[13.5px] font-semibold">
            {inv.number ?? 'Invoice'}
            <Chip tone={tone}>{inv.status}</Chip>
          </p>
          <p className="mt-0.5 text-[12px] text-muted">
            {inv.paidAt ? `Paid ${day(inv.paidAt)}` : `Issued ${day(inv.issuedAt)}`}
            {inv.amountOutstanding > 0 && ` · ${exact(inv.amountOutstanding, inv.currency)} outstanding`}
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          <p className="text-[14px] font-bold tabular-nums">{exact(inv.total, inv.currency)}</p>
          {inv.lines?.length > 0 && (
            <button
              onClick={() => setOpen((v) => !v)}
              className="rounded-pill px-2 py-1 text-[11.5px] font-semibold text-ink-soft transition hover:bg-panel"
              aria-expanded={open}
            >
              {open ? 'Hide' : 'Why?'}
            </button>
          )}
          {inv.invoicePdfUrl && (
            <a href={inv.invoicePdfUrl} target="_blank" rel="noreferrer"
              className="rounded-pill px-2 py-1 text-[11.5px] font-semibold text-ink-soft transition hover:bg-panel">
              PDF
            </a>
          )}
        </div>
      </div>

      {open && (
        <div className="mt-2 rounded-2xl bg-panel px-3 py-2">
          {inv.lines.map((l, i) => (
            <div key={i} className="flex items-start justify-between gap-3 py-1">
              <p className="text-[12px] leading-snug text-ink-soft">{l.label}</p>
              <p className="shrink-0 text-[12px] font-semibold tabular-nums">{exact(l.amount, inv.currency)}</p>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

export default function Billing() {
  const { data, isLoading, isError, refetch } = useBilling()
  const [params] = useSearchParams()
  const [busy, setBusy] = useState(null)
  const [problem, setProblem] = useState(null)
  const [notice, setNotice] = useState(null)

  const checkout = params.get('checkout')

  /** Both Stripe actions do the same thing: ask the API for a URL and hand the browser over. */
  const goToStripe = async (path, body = {}) => {
    setBusy(path)
    setProblem(null)
    try {
      const { url } = await api.post(`/billing/${path}`, body).then(unwrap)
      window.location.assign(url)
    } catch (err) {
      setProblem(err.response?.data?.message ?? 'Could not reach the payment provider. Please try again.')
      setBusy(null)
    }
  }

  if (isLoading) {
    return (
      <div className="grid place-items-center py-24">
        <p className="text-[13px] text-muted">Loading your billing…</p>
      </div>
    )
  }

  if (isError) {
    return (
      <Card>
        <CardTitle title="Billing" />
        <EmptyState message="Your billing details could not be loaded. Please try again shortly." />
        <div className="mt-3 flex justify-center">
          <PillButton variant="outline" onClick={() => refetch()}>Retry</PillButton>
        </div>
      </Card>
    )
  }

  const s = data ?? {}
  const cur = s.currency ?? 'USD'
  const usageRatio = s.includedMinutes > 0 ? s.minutesUsed / s.includedMinutes : 0

  const startCheckout = (planId) => goToStripe('checkout-session', { planId })

  /** Switching does not leave the app: nothing is charged now, so there is no Stripe page to
   *  visit. The summary is refetched so the queued move appears where the tiers are. */
  const switchPlan = async (planId) => {
    setBusy('change-plan')
    setProblem(null)
    setNotice(null)
    try {
      const result = await api.post('/billing/change-plan', { planId }).then(unwrap)
      setNotice(result?.message ?? 'Your plan will change at your next billing cycle.')
      await refetch()
    } catch (err) {
      setProblem(err.response?.data?.message ?? 'Your plan could not be changed. Please try again.')
    } finally {
      setBusy(null)
    }
  }

  if (!s.hasSubscription) {
    return (
      <div className="mx-auto max-w-5xl space-y-4">
        <header>
          <h1 className="text-[22px] font-bold">Billing</h1>
          <p className="mt-0.5 text-[13px] text-muted">What you are charged, and why.</p>
        </header>
        {problem && (
          <div className="rounded-card bg-danger-soft px-4 py-3 text-[13px] font-medium text-danger">{problem}</div>
        )}
        <Card>
          <EmptyState message="No plan is set up on your account yet, so nothing is being charged. The plans on offer are below." />
        </Card>
        <PlanChooser
          currentPlanId={s.planId}
          canSubscribe={s.canSubscribe}
          busy={busy !== null}
          onCheckout={startCheckout}
          onSwitch={switchPlan}
        />
      </div>
    )
  }

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <header className="flex flex-wrap items-end justify-between gap-x-4 gap-y-2">
        <div>
          <h1 className="text-[22px] font-bold">Billing</h1>
          <p className="mt-0.5 text-[13px] text-muted">What you are charged, and why.</p>
        </div>
        <div className="flex flex-wrap gap-2">
          {s.autoCollecting && (
            <PillButton variant="outline" disabled={busy !== null}
              onClick={() => goToStripe('portal-session')}>
              {busy === 'portal-session' ? 'Opening…' : 'Manage payment method'}
            </PillButton>
          )}
        </div>
      </header>

      {checkout === 'success' && (
        <div className="rounded-card bg-mint px-4 py-3 text-[13px] font-medium">
          Thank you — your payment method is set up. It can take a moment to appear below.
        </div>
      )}
      {checkout === 'cancelled' && (
        <div className="rounded-card bg-cream px-4 py-3 text-[13px] font-medium">
          Checkout was cancelled. Nothing has been charged.
        </div>
      )}
      {notice && (
        <div className="rounded-card bg-mint px-4 py-3 text-[13px] font-medium">{notice}</div>
      )}
      {problem && (
        <div className="rounded-card bg-danger-soft px-4 py-3 text-[13px] font-medium text-danger">{problem}</div>
      )}

      {s.agentRestricted && (
        <div className="rounded-card bg-danger-soft px-4 py-3">
          <p className="text-[13px] font-semibold text-danger">Your AI receptionist is not taking calls</p>
          <p className="mt-0.5 text-[12.5px] text-danger">
            {s.agentRestrictedReason ?? 'Your provider has paused it.'} Settling the balance below, or
            contacting your provider, will restore it.
          </p>
        </div>
      )}

      {/*
        The tiers, in whichever of its two jobs applies: choosing one to start paying, or moving
        between them once Stripe is collecting. Both are the same list, so they are the same card.
      */}
      <PlanChooser
        currentPlanId={s.planId}
        canSubscribe={s.canSubscribe}
        canChangePlan={s.canChangePlan}
        pendingPlanId={s.pendingPlanId}
        pendingPlanName={s.pendingPlanName}
        periodEnd={s.currentPeriodEnd}
        busy={busy !== null}
        onCheckout={startCheckout}
        onSwitch={switchPlan}
      />

      {/* ---- the next bill, itemised ---- */}
      <div className="grid gap-4 @3xl:grid-cols-[1.4fr_1fr]">
        <Card>
          <CardTitle
            title="Your next bill"
            subtitle={`Covering ${dayShort(s.currentPeriodStart)} – ${day(s.currentPeriodEnd)}`}
          />
          <div>
            {s.upcomingCharges?.map((line, i) => (
              <ChargeRow key={i} line={line} currency={cur} />
            ))}
          </div>
          <div className="mt-3 flex items-baseline justify-between gap-4 border-t-2 border-ink/10 pt-3">
            <div>
              <p className="text-[13.5px] font-bold">Estimated total</p>
              <p className="mt-0.5 text-[11.5px] text-muted">
                {s.minutesOver > 0
                  ? 'The extra-minutes line keeps moving until the period ends.'
                  : 'Final unless you go past your included minutes.'}
              </p>
            </div>
            <p className="text-[22px] font-bold tabular-nums">{exact(s.estimatedNextInvoice, cur)}</p>
          </div>

          {s.pendingOverageAmount > 0 && (
            <p className="mt-3 rounded-2xl bg-cream px-3 py-2 text-[12px] leading-snug">
              You went <strong>{s.pendingOverageMinutes.toLocaleString()} minutes</strong> past your plan
              last period. As agreed, that is not billed separately — it is added to the invoice above.
            </p>
          )}
        </Card>

        {/* ---- minutes ---- */}
        <Card>
          <CardTitle
            title="Minutes this period"
            subtitle={s.metered ? `${fmtMinutes(s.includedMinutes)} included` : 'Not metered on minutes'}
          />
          {s.metered ? (
            <>
              <GaugeMeter
                used={s.minutesUsed}
                allowance={s.includedMinutes}
                caption={
                  s.minutesOver > 0
                    ? `${s.minutesOver.toLocaleString()} min over — ${exact(s.projectedOverageAmount, cur)} so far`
                    : `Resets ${day(s.currentPeriodEnd)}`
                }
              />
              <dl className="mt-3 space-y-2 text-[12.5px]">
                <div className="flex justify-between">
                  <dt className="text-muted">Used</dt>
                  <dd className="font-semibold tabular-nums">{fmtMinutes(s.minutesUsed)}</dd>
                </div>
                <div className="flex justify-between">
                  <dt className="text-muted">Included in {s.planName}</dt>
                  <dd className="font-semibold tabular-nums">{fmtMinutes(s.includedMinutes)}</dd>
                </div>
                <div className="flex justify-between">
                  <dt className="text-muted">Each extra minute</dt>
                  <dd className="font-semibold tabular-nums">
                    {s.overageRatePerMinute > 0 ? `${s.overageRatePerMinute} ${cur}` : 'Not charged'}
                  </dd>
                </div>
              </dl>
              <BarMeter
                ratio={usageRatio}
                tone={
                  s.minutesOver > 0
                    ? 'var(--color-crit)'
                    : usageRatio > 0.8
                      ? 'var(--color-warn)'
                      : 'var(--color-viz-fill)'
                }
                className="mt-3"
              />
            </>
          ) : (
            <div className="py-4">
              <p className="text-[30px] font-bold tabular-nums">{fmtMinutes(s.minutesUsed)}</p>
              <p className="mt-1 text-[12.5px] text-muted">
                Your {s.planName} plan is a flat {exact(s.planAmount, cur)} per{' '}
                {String(s.billingCycle).toLowerCase() === 'yearly' ? 'year' : 'month'}, however much the
                receptionist talks.
              </p>
            </div>
          )}
        </Card>
      </div>

      {/* ---- previous periods ---- */}
      <Card>
        <CardTitle
          title="Previous periods"
          subtitle="What you used, and what it came to"
        />
        {s.history?.length > 0 ? (
          <div>{s.history.map((p, i) => <PeriodRow key={i} p={p} />)}</div>
        ) : (
          <EmptyState message="Nothing yet — your first period is still running." />
        )}
      </Card>

      {/* ---- invoices ---- */}
      <Card>
        <CardTitle
          title="Invoices"
          subtitle={s.autoCollecting ? 'Collected automatically' : 'Raised by your provider'}
          action={
            s.autoCollecting ? (
              <PillButton variant="outline" disabled={busy !== null}
                onClick={() => goToStripe('portal-session')}>
                All invoices
              </PillButton>
            ) : null
          }
        />
        {s.invoices?.length > 0 ? (
          <div>{s.invoices.map((inv, i) => <InvoiceRow key={i} inv={inv} />)}</div>
        ) : (
          <EmptyState message="No invoices have been raised on this account yet." />
        )}
      </Card>
    </div>
  )
}
