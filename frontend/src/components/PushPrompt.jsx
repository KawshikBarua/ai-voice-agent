import { useEffect, useState } from 'react'
import { AnimatePresence, motion } from 'framer-motion'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '../api/client'
import {
  currentSubscription, enablePush, iosNeedsInstall, permission, pushSupported,
} from '../lib/push'
import { PillButton } from './ui'

/**
 * Asks, once, to turn notifications on.
 *
 * Everything needed to reach a closed app was already built — the worker, the subscription, the
 * VAPID keys, the delivery — but a device is only ever enrolled from Settings → Notifications, and
 * nobody goes looking there. The result was an owner who had signed in, believed they would be
 * told about a booking, and had no device registered to be told on. This is the missing step: the
 * one place the person will actually see it.
 *
 * It is a banner rather than an automatic prompt on purpose. Browsers require a user gesture
 * before Notification.requestPermission() will show anything, and a permission dialog fired at a
 * page nobody asked for it on is the fastest way to get "Block" pressed — after which this device
 * cannot be reached again without the user digging through browser settings.
 */

const DISMISSED_KEY = 'frontly-push-prompt-dismissed'

const safely = (fn, fallback = null) => { try { return fn() } catch { return fallback } }

export default function PushPrompt() {
  const queryClient = useQueryClient()

  const { data: config } = useQuery({
    queryKey: ['push-config'],
    queryFn: () => api.get('/notifications/config').then(unwrap),
    staleTime: Infinity,
  })

  // 'checking' until the browser has been asked what it already has, so the banner never flashes
  // up at a device that is in fact already subscribed.
  const [state, setState] = useState('checking')
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState(null)
  const [dismissed, setDismissed] = useState(() => safely(() => localStorage.getItem(DISMISSED_KEY) === '1', false))

  useEffect(() => {
    let cancelled = false

    const read = async () => {
      // iOS delivers Web Push only to a site added to the Home Screen. Saying "your browser cannot
      // do this" there would be wrong: one step fixes it, so it gets its own message.
      if (!pushSupported()) {
        if (!cancelled) setState(iosNeedsInstall() ? 'ios' : 'unsupported')
        return
      }
      // Already blocked. A "Turn on" button cannot lift that — only the browser's own site
      // settings can — and Settings → Notifications explains how, so this stays out of the way.
      if (permission() === 'denied') {
        if (!cancelled) setState('denied')
        return
      }
      const subscription = await currentSubscription()
      if (!cancelled) setState(subscription ? 'on' : 'off')
    }

    read()
    return () => { cancelled = true }
  }, [])

  const turnOn = async () => {
    setBusy(true); setNote(null)
    try {
      const result = await enablePush(config?.publicKey)
      if (result.ok) {
        setState('on')
        queryClient.invalidateQueries({ queryKey: ['push-devices'] })
        return
      }
      setState(result.reason === 'denied' ? 'denied' : 'off')
      setNote(
        result.reason === 'denied'
          ? 'Your browser is blocking notifications for this site. Allow them in the padlock menu beside the address bar, then try again.'
          : result.reason === 'not-configured'
            ? 'Notifications are not set up on this server yet.'
            : 'No permission was given, so this device will not be notified.',
      )
    } catch {
      setNote('Could not turn notifications on for this device. Please try again.')
    } finally {
      setBusy(false)
    }
  }

  const dismiss = () => {
    setDismissed(true)
    safely(() => localStorage.setItem(DISMISSED_KEY, '1'))
  }

  // Nothing to offer: the server has no keys, this device is already enrolled or cannot be, or
  // the person has said no thank you. Settings remains the way back in for all of them.
  const offer = state === 'off' || state === 'ios'
  if (!config?.enabled || !offer || dismissed) return null

  return (
    <AnimatePresence>
      <motion.section
        initial={{ opacity: 0, y: -8 }}
        animate={{ opacity: 1, y: 0 }}
        exit={{ opacity: 0, y: -8 }}
        transition={{ duration: 0.28, ease: 'easeOut' }}
        className="mb-4 rounded-card border border-line bg-card p-4 shadow-sm sm:p-5"
      >
        <div className="flex flex-wrap items-start gap-3">
          <span className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-brand-soft">
            <svg viewBox="0 0 24 24" className="h-[18px] w-[18px]" fill="none" stroke="currentColor"
              strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
              <path d="M18 8a6 6 0 1 0-12 0c0 7-3 9-3 9h18s-3-2-3-9" />
              <path d="M13.7 21a2 2 0 0 1-3.4 0" />
            </svg>
          </span>

          <div className="min-w-0 flex-1">
            <p className="text-sm font-bold">Get told the moment a booking comes in</p>
            <p className="mt-0.5 text-xs text-ink-soft">
              {state === 'ios'
                ? 'On iPhone and iPad this works once Frontly is added to your Home Screen: tap Share, then "Add to Home Screen", and open it from there.'
                : 'Turn on notifications for this device and your AI will reach you when a call books someone in — even with this app closed.'}
            </p>
            {note && <p className="mt-2 text-xs text-ink-soft">{note}</p>}
          </div>

          <div className="flex shrink-0 items-center gap-2">
            {state === 'off' && (
              <PillButton variant="primary" onClick={turnOn} disabled={busy}>
                {busy ? 'Turning on…' : 'Turn on'}
              </PillButton>
            )}
            <PillButton variant="outline" onClick={dismiss}>Not now</PillButton>
          </div>
        </div>
      </motion.section>
    </AnimatePresence>
  )
}
