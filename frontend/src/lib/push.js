import { api } from '../api/client'

/**
 * Browser notifications.
 *
 * The problem: nobody sits in the dashboard, so a booking taken overnight is invisible until the
 * next sign-in. Web Push is the only channel that reaches a phone with the app closed and costs
 * nothing per message — the server signs each delivery with a key pair it generated for itself,
 * and the browser vendor's push service carries it.
 *
 * Everything here is best-effort by design. A browser that does not support push, a user who
 * declines permission, or a deployment with no keys configured must all leave the app working
 * exactly as before; the dashboard's own alert queue is the fallback in every one of those cases.
 */

export const pushSupported = () =>
  typeof navigator !== 'undefined' &&
  'serviceWorker' in navigator &&
  'PushManager' in window &&
  'Notification' in window

/** Running from the home screen rather than a browser tab. */
export const isStandalone = () =>
  window.matchMedia?.('(display-mode: standalone)').matches || window.navigator.standalone === true

/**
 * iOS only delivers Web Push to a site the user has added to their Home Screen — in a normal
 * Safari tab the APIs are missing entirely. Worth detecting, because "your browser cannot do
 * this" is wrong and unhelpful advice when one extra step would fix it.
 */
export const iosNeedsInstall = () =>
  /iphone|ipad|ipod/i.test(navigator.userAgent) && !isStandalone() && !pushSupported()

export const permission = () => (pushSupported() ? Notification.permission : 'unsupported')

/**
 * A name for this device, so somebody with a phone and a laptop registered can tell which is
 * which before switching one off. Coarse on purpose — enough to recognise, not a fingerprint.
 */
function describeDevice() {
  const ua = navigator.userAgent
  const platform =
    /iphone/i.test(ua) ? 'iPhone'
      : /ipad/i.test(ua) ? 'iPad'
        : /android/i.test(ua) ? 'Android'
          : /macintosh/i.test(ua) ? 'Mac'
            : /windows/i.test(ua) ? 'Windows'
              : 'This device'
  const browser =
    /edg\//i.test(ua) ? 'Edge'
      : /chrome|crios/i.test(ua) ? 'Chrome'
        : /firefox|fxios/i.test(ua) ? 'Firefox'
          : /safari/i.test(ua) ? 'Safari'
            : 'browser'
  return `${platform} · ${browser}`
}

/** The server hands out its public key base64url-encoded; subscribe() wants raw bytes. */
function decodeKey(base64Url) {
  const padded = (base64Url + '='.repeat((4 - (base64Url.length % 4)) % 4))
    .replace(/-/g, '+')
    .replace(/_/g, '/')
  const raw = atob(padded)
  return Uint8Array.from([...raw].map((c) => c.charCodeAt(0)))
}

/**
 * Registers the worker that receives pushes. Safe to call on every load: the browser keeps one
 * registration per scope and this resolves to the existing one.
 */
export async function registerServiceWorker() {
  if (!pushSupported()) return null
  try {
    return await navigator.serviceWorker.register('/sw.js', { scope: '/' })
  } catch {
    // A blocked or unavailable worker (private mode, some enterprise policies) is not an error
    // worth surfacing — it just means this device will not be notified.
    return null
  }
}

export async function currentSubscription() {
  if (!pushSupported()) return null
  const registration = await navigator.serviceWorker.getRegistration('/')
  return registration ? registration.pushManager.getSubscription() : null
}

/**
 * Asks permission, subscribes, and tells the server where to reach this device.
 *
 * Returns a plain outcome rather than throwing, because every failure here is a normal thing a
 * person might do — most often simply saying no to the browser's prompt.
 */
export async function enablePush(publicKey, { urgentOnly = null } = {}) {
  if (!pushSupported()) return { ok: false, reason: 'unsupported' }
  if (!publicKey) return { ok: false, reason: 'not-configured' }

  const granted = await Notification.requestPermission()
  if (granted !== 'granted') return { ok: false, reason: granted === 'denied' ? 'denied' : 'dismissed' }

  const registration = (await registerServiceWorker()) ?? (await navigator.serviceWorker.ready)

  // An existing subscription made against a different (older) server key can never be decrypted
  // by the current one, and the browser refuses to re-subscribe over it. Dropping it first is
  // what makes rotating keys — or moving between deployments — recoverable without clearing
  // site data by hand.
  const existing = await registration.pushManager.getSubscription()
  if (existing) await existing.unsubscribe().catch(() => {})

  const subscription = await registration.pushManager.subscribe({
    // Non-negotiable in every current browser: a push must always show a notification. That is
    // fine here — there is nothing this app would want to receive silently.
    userVisibleOnly: true,
    applicationServerKey: decodeKey(publicKey),
  })

  const json = subscription.toJSON()
  await api.post('/notifications/subscribe', {
    endpoint: json.endpoint,
    p256dh: json.keys.p256dh,
    auth: json.keys.auth,
    label: describeDevice(),
    urgentOnly,
  })

  return { ok: true }
}

/**
 * Re-sends an existing subscription to the server on load.
 *
 * Push endpoints and keys are rotated by the browser from time to time, and a stale row means
 * every later alert fails silently — the worst possible failure for a feature nobody watches.
 * Sending no `urgentOnly` leaves whatever preference is stored alone.
 */
export async function refreshSubscription() {
  const subscription = await currentSubscription()
  if (!subscription) return false

  const json = subscription.toJSON()
  await api.post('/notifications/subscribe', {
    endpoint: json.endpoint,
    p256dh: json.keys.p256dh,
    auth: json.keys.auth,
    label: describeDevice(),
    urgentOnly: null,
  })
  return true
}

/** Stops this device being notified, at both ends. */
export async function disablePush() {
  const subscription = await currentSubscription()
  if (!subscription) return

  const { endpoint } = subscription.toJSON()
  await subscription.unsubscribe().catch(() => {})

  // The server row goes too. Leaving it would keep this deployment pushing to an endpoint the
  // browser has abandoned, which fails quietly on every alert from here on.
  await api.post('/notifications/unsubscribe', { endpoint, p256dh: '', auth: '' }).catch(() => {})
}
