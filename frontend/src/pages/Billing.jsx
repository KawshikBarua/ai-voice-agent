import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { api, unwrap } from '../api/client'
import { useUsage } from '../api/usage'
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
        <span className="text-base font-bold">{plan.name}</span>
        {isCurrent && <Chip tone="mint">Your plan</Chip>}
      </span>
      <span className="mt-1 block text-lg font-bold tabular-nums">
        {exact(plan.amount, plan.currency)}
        <span className="ml-1 text-xs font-medium text-muted">per {cyclePer(plan.billingCycle)}</span>
      </span>
      {plan.description && (
        <span className="mt-1 block text-sm leading-snug text-ink-soft">{plan.description}</span>
      )}
      <span className="mt-1.5 block text-xs leading-relaxed text-muted">
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
      {note && <span className="mt-2 block text-xs font-medium text-muted">{note}</span>}
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
        <div className="mb-3 rounded-2xl bg-mint px-3 py-2 text-sm leading-snug">
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
/**
 * A payment that has been started and not yet confirmed.
 *
 * This is the banner for the customer whose connection dropped on Stripe's page. Their money is
 * already safe — the webhook, their return to this page, and the reconciliation sweep are three
 * independent ways it lands — but without being told that, a billing page still reading "no plan"
 * gives them every reason to pay a second time.
 *
 * After a quarter of an hour the wording changes rather than escalating. By then it is more likely
 * they closed the tab at the card form than that anything is wrong, and neither case is helped by
 * an alarming message.
 */
function PendingPaymentNotice({ pending }) {
  if (!pending) return null

  return (
    <div className="rounded-card bg-cream px-4 py-3 text-sm">
      <p className="font-medium">
        {pending.isStale
          ? `A payment for ${pending.planName} has not been confirmed yet.`
          : `Confirming your ${pending.planName} payment…`}
      </p>
      <p className="mt-1 text-muted">
        {pending.isStale
          ? `Started ${day(pending.startedAt)}. If you completed payment it will appear here on its own —
             you have not been charged twice, and there is nothing to do. If you did not finish, you can
             choose a plan below and start again.`
          : `We are waiting on ${exact(pending.amount, pending.currency)} from Stripe. You can leave this
             page — your plan will be set up as soon as the payment clears.`}
      </p>
    </div>
  )
}

/**
 * The free trial, while it is running and once it has run out.
 *
 * The second half is the one that matters: when a trial ends with no plan behind it the AI
 * receptionist stops answering, and a customer whose phone has gone quiet has to be able to find
 * out why on this page rather than by ringing their own number.
 */
function TrialNotice({ summary }) {
  const { onTrial, trialExpired, trialEndsAt, trialDaysRemaining, hasSubscription } = summary

  if (onTrial) {
    return (
      <div className="rounded-card bg-lavender px-4 py-3 text-sm">
        <p className="font-medium">
          Free trial — {trialDaysRemaining} day{trialDaysRemaining === 1 ? '' : 's'} left
        </p>
        <p className="mt-1 text-muted">
          Your AI receptionist is answering at no charge until {day(trialEndsAt)}.
          {!hasSubscription && ' Choose a plan before then to keep it taking calls.'}
        </p>
      </div>
    )
  }

  // A plan taken out during the trial has already superseded it, so the expiry is not news.
  if (!trialExpired || hasSubscription) return null

  return (
    <div className="rounded-card bg-danger-soft px-4 py-3">
      <p className="text-sm font-semibold text-danger">Your free trial has ended</p>
      <p className="mt-0.5 text-sm text-danger">
        It ran out on {day(trialEndsAt)}, so your AI receptionist has stopped taking calls. Choosing
        a plan below switches it back on.
      </p>
    </div>
  )
}

function ChargeRow({ line, currency }) {
  return (
    <div className="flex items-start justify-between gap-4 border-b border-line/60 py-3 last:border-0">
      <div className="min-w-0">
        <p className="text-base font-semibold">
          {line.label}
          {!line.isFinal && (
            <span className="ml-2 align-middle text-2xs font-medium uppercase tracking-wide text-muted">
              so far
            </span>
          )}
        </p>
        {line.detail && <p className="mt-0.5 text-xs leading-snug text-muted">{line.detail}</p>}
      </div>
      <p className="shrink-0 text-base font-bold tabular-nums">{exact(line.amount, currency)}</p>
    </div>
  )
}

/** One closed period, spelled out the same way whether it cost extra or not. */
function PeriodRow({ p }) {
  const over = p.overageMinutes > 0
  return (
    <div className="border-b border-line/60 py-3 last:border-0">
      <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
        <p className="text-base font-semibold">
          {dayShort(p.periodStart)} – {day(p.periodEnd)}
        </p>
        <p className="text-base font-bold tabular-nums">{exact(p.total, p.currency)}</p>
      </div>
      <p className="mt-1 text-xs leading-relaxed text-muted">
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
          <p className="flex items-center gap-2 text-base font-semibold">
            {inv.number ?? 'Invoice'}
            <Chip tone={tone}>{inv.status}</Chip>
          </p>
          <p className="mt-0.5 text-xs text-muted">
            {inv.paidAt ? `Paid ${day(inv.paidAt)}` : `Issued ${day(inv.issuedAt)}`}
            {inv.amountOutstanding > 0 && ` · ${exact(inv.amountOutstanding, inv.currency)} outstanding`}
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          <p className="text-base font-bold tabular-nums">{exact(inv.total, inv.currency)}</p>
          {inv.lines?.length > 0 && (
            <button
              onClick={() => setOpen((v) => !v)}
              className="rounded-pill px-2 py-1 text-xs font-semibold text-ink-soft transition hover:bg-panel"
              aria-expanded={open}
            >
              {open ? 'Hide' : 'Why?'}
            </button>
          )}
          {inv.invoicePdfUrl && (
            <a href={inv.invoicePdfUrl} target="_blank" rel="noreferrer"
              className="rounded-pill px-2 py-1 text-xs font-semibold text-ink-soft transition hover:bg-panel">
              PDF
            </a>
          )}
        </div>
      </div>

      {open && (
        <div className="mt-2 rounded-2xl bg-panel px-3 py-2">
          {inv.lines.map((l, i) => (
            <div key={i} className="flex items-start justify-between gap-3 py-1">
              <p className="text-xs leading-snug text-ink-soft">{l.label}</p>
              <p className="shrink-0 text-xs font-semibold tabular-nums">{exact(l.amount, inv.currency)}</p>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

export default function Billing() {
  const { data, isLoading, isError, isPaused, refetch } = useBilling()
  const { data: usage } = useUsage()
  const queryClient = useQueryClient()
  const [params, setParams] = useSearchParams()
  const [busy, setBusy] = useState(null)
  const [problem, setProblem] = useState(null)
  const [notice, setNotice] = useState(null)
  const [confirming, setConfirming] = useState(false)

  const checkout = params.get('checkout')
  const sessionId = params.get('session_id')
  const confirmed = useRef(null)

  /**
   * Coming back from Stripe, having paid.
   *
   * The webhook applies the purchase too, but it cannot be what the customer waits on: it needs a
   * publicly reachable endpoint and a matching signing secret, and where either is missing the
   * money leaves their account while this page keeps saying no plan is set up. So the page confirms
   * for itself — the server reads the session straight from Stripe — and the plan and its minutes
   * are there by the time this redraws.
   *
   * The ref is what stops a redraw firing a second request. There is deliberately no cleanup flag
   * cancelling the handlers below: an effect that re-runs — which StrictMode does on every mount in
   * development, and a remount does in production — would set that flag on the only request in
   * flight, while the ref sent the second run home. Nothing then cleared `confirming`, so the page
   * sat on "confirming your payment" forever with the plan chooser hidden behind it: a customer who
   * had just paid, and one who had not yet, both left with no way forward. Settling state after an
   * unmount is a no-op in React 18, which is a far cheaper price than that.
   */
  useEffect(() => {
    if (!sessionId || confirmed.current === sessionId) return
    confirmed.current = sessionId

    setConfirming(true)
    api.post('/billing/confirm-checkout', { sessionId })
      .then(async (res) => {
        setNotice(res.data?.message ?? 'Your plan is active.')
        // Minutes and the plan tile live on other screens too, so those are dropped rather than
        // left to go stale behind a customer who has just started paying.
        await Promise.all([refetch(), queryClient.invalidateQueries({ queryKey: ['billing-usage'] })])
        queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      })
      .catch((err) => {
        // The webhook may still land, so this is not stated as a failure of the payment.
        setProblem(err.response?.data?.message
          ?? 'We could not confirm your payment just yet. If you were charged it will appear here shortly.')
      })
      .finally(() => {
        setConfirming(false)
        // Out of the URL once used: a reload should not look like a second purchase.
        setParams((current) => {
          const next = new URLSearchParams(current)
          next.delete('session_id')
          return next
        }, { replace: true })
      })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId])

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
        <p className="text-sm text-muted">Loading your billing…</p>
      </div>
    )
  }

  /*
    Nothing loaded — which is not the same as nothing to bill.

    An offline browser makes React Query *pause* the query rather than fail it, so isError stays
    false and the data stays undefined. Falling through on that emptiness reached the "no plan is
    set up on your account" screen, which tells a customer who is paying every month that they are
    on nothing, purely because their connection dropped. Anything short of real data has to stop
    here.
  */
  if (isError || isPaused || !data) {
    return (
      <Card>
        <CardTitle title="Billing" />
        <EmptyState message={isPaused
          ? 'You appear to be offline, so your billing details could not be loaded. Nothing has changed on your account.'
          : 'Your billing details could not be loaded. Nothing has changed on your account — please try again shortly.'}
        />
        <div className="mt-3 flex justify-center">
          <PillButton variant="outline" onClick={() => refetch()}>Retry</PillButton>
        </div>
      </Card>
    )
  }

  const s = data ?? {}
  const cur = s.currency ?? 'USD'

  // The polled figures win over the summary's, which were true when the page loaded. They are the
  // same numbers measured the same way — this is only about which read is the most recent one.
  const live = usage?.hasSubscription ? usage : null
  const metered = live?.metered ?? s.metered
  const minutesUsed = live?.minutesUsed ?? s.minutesUsed ?? 0
  const minutesOver = live?.minutesOver ?? s.minutesOver ?? 0
  const includedMinutes = live?.includedMinutes ?? s.includedMinutes ?? 0
  const projectedOverage = live?.projectedOverageAmount ?? s.projectedOverageAmount ?? 0
  const usageRatio = includedMinutes > 0 ? minutesUsed / includedMinutes : 0

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
          <h1 className="font-display text-xl font-semibold tracking-[-0.01em]">Billing</h1>
          <p className="mt-0.5 text-sm text-muted">What you are charged, and why.</p>
        </header>
        {problem && (
          <div className="rounded-card bg-danger-soft px-4 py-3 text-sm font-medium text-danger">{problem}</div>
        )}
        {notice && (
          <div className="rounded-card bg-mint px-4 py-3 text-sm font-medium">{notice}</div>
        )}
        <PendingPaymentNotice pending={s.pendingPayment} />
        <TrialNotice summary={s} />
        <Card>
          {/* Someone who has just paid is not "not set up" — they are mid-setup, and saying the
              wrong one of those to a customer who has been charged is how support tickets start. */}
          <EmptyState
            message={confirming
              ? 'Setting up your plan — this takes a moment.'
              : s.pendingPayment
                ? 'Your plan will appear here as soon as the payment is confirmed.'
                : s.onTrial
                  ? 'You are on a free trial, so nothing is being charged yet. The plans on offer are below.'
                  : 'No plan is set up on your account yet, so nothing is being charged. The plans on offer are below.'}
          />
        </Card>
        {!confirming && (
          <PlanChooser
            currentPlanId={s.planId}
            canSubscribe={s.canSubscribe}
            busy={busy !== null}
            onCheckout={startCheckout}
            onSwitch={switchPlan}
          />
        )}
      </div>
    )
  }

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <header className="flex flex-wrap items-end justify-between gap-x-4 gap-y-2">
        <div>
          <h1 className="font-display text-xl font-semibold tracking-[-0.01em]">Billing</h1>
          <p className="mt-0.5 text-sm text-muted">What you are charged, and why.</p>
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

      {/* Only when the confirmation has nothing more specific to say — otherwise the customer reads
          two banners about one payment, and if the confirmation failed they read a cheerful one
          directly above the failure. */}
      {checkout === 'success' && !notice && !problem && !confirming && (
        <div className="rounded-card bg-mint px-4 py-3 text-sm font-medium">
          Thank you — your payment method is set up.
        </div>
      )}
      {confirming && (
        <div className="rounded-card bg-cream px-4 py-3 text-sm font-medium">
          Confirming your payment with Stripe…
        </div>
      )}
      {checkout === 'cancelled' && (
        <div className="rounded-card bg-cream px-4 py-3 text-sm font-medium">
          Checkout was cancelled. Nothing has been charged.
        </div>
      )}
      {/* A plan change paid for but not yet confirmed belongs here too, not only on the
          no-plan screen — the customer is equally in the dark either way. */}
      {!confirming && <PendingPaymentNotice pending={s.pendingPayment} />}

      <TrialNotice summary={s} />
      {notice && (
        <div className="rounded-card bg-mint px-4 py-3 text-sm font-medium">{notice}</div>
      )}
      {problem && (
        <div className="rounded-card bg-danger-soft px-4 py-3 text-sm font-medium text-danger">{problem}</div>
      )}

      {s.agentRestricted && (
        <div className="rounded-card bg-danger-soft px-4 py-3">
          <p className="text-sm font-semibold text-danger">Your AI receptionist is not taking calls</p>
          <p className="mt-0.5 text-sm text-danger">
            {s.agentRestrictedReason ?? 'Your provider has paused it.'} Settling the balance below, or
            contacting your provider, will restore it.
          </p>
        </div>
      )}

      {/* Said before the customer leaves for Stripe, because the amount on the payment page is the
          plan plus this — being charged more than you expected is worse than being told first. */}
      {s.canSubscribe && s.pendingOverageAmount > 0 && (
        <div className="rounded-card bg-cream px-4 py-3 text-sm">
          <p className="font-medium">
            {exact(s.pendingOverageAmount, cur)} of extra minutes is still owed from earlier periods
          </p>
          <p className="mt-1 text-muted">
            It is added to the plan price when you pay, so a single payment settles everything —
            you will see both parts itemised before you confirm.
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
              <p className="text-base font-bold">Estimated total</p>
              <p className="mt-0.5 text-xs text-muted">
                {s.minutesOver > 0
                  ? 'The extra-minutes line keeps moving until the period ends.'
                  : 'Final unless you go past your included minutes.'}
              </p>
            </div>
            <p className="text-xl font-bold tabular-nums">{exact(s.estimatedNextInvoice, cur)}</p>
          </div>

          {s.pendingOverageAmount > 0 && (
            <p className="mt-3 rounded-2xl bg-cream px-3 py-2 text-xs leading-snug">
              You went <strong>{s.pendingOverageMinutes.toLocaleString()} minutes</strong> past your plan
              last period. As agreed, that is not billed separately — it is added to the invoice above.
            </p>
          )}
        </Card>

        {/* ---- minutes ---- */}
        <Card>
          <CardTitle
            title="Minutes this period"
            subtitle={metered ? `${fmtMinutes(includedMinutes)} included` : 'Not metered on minutes'}
          />
          {metered ? (
            <>
              <GaugeMeter
                used={minutesUsed}
                allowance={includedMinutes}
                caption={
                  minutesOver > 0
                    ? `${minutesOver.toLocaleString()} min over — ${exact(projectedOverage, cur)} so far`
                    : `Resets ${day(s.currentPeriodEnd)}`
                }
              />
              <dl className="mt-3 space-y-2 text-sm">
                <div className="flex justify-between">
                  <dt className="text-muted">Used</dt>
                  <dd className="font-semibold tabular-nums">{fmtMinutes(minutesUsed)}</dd>
                </div>
                <div className="flex justify-between">
                  <dt className="text-muted">Included in {s.planName}</dt>
                  <dd className="font-semibold tabular-nums">{fmtMinutes(includedMinutes)}</dd>
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
                  minutesOver > 0
                    ? 'var(--color-crit)'
                    : usageRatio > 0.8
                      ? 'var(--color-warn)'
                      : 'var(--color-viz-fill)'
                }
                className="mt-3"
              />
              {/* Stated, because it is the single most common thing a customer queries: their call
                  log shows a handful of seconds and their bill shows a minute. The rule has always
                  been per-call round-up; only saying so is new. */}
              <p className="mt-3 text-xs text-muted">
                Each call is rounded up to the next whole minute, so a 20-second call counts as one.
              </p>
            </>
          ) : (
            <div className="py-4">
              <p className="text-3xl font-bold tabular-nums">{fmtMinutes(minutesUsed)}</p>
              <p className="mt-1 text-sm text-muted">
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
