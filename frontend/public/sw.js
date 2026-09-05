/*
 * Frontly service worker — notifications only.
 *
 * Deliberately no offline caching. A cache here would have to be versioned and invalidated
 * against Vite's hashed builds, and the classic failure of that (an owner staring at a stale
 * dashboard after a deploy) is far worse than the thing it buys. The only reason this file
 * exists is that a push cannot be received without a service worker: it is the one piece of
 * the app the browser will run when every tab is closed.
 *
 * That is also what makes the whole feature free. The message arrives via the browser vendor's
 * push service, already encrypted to this device's keys, and is handed straight to the handler
 * below — no polling, no SMS, no third-party account anywhere in the chain.
 */

// Take over immediately rather than waiting for every tab to close, so turning notifications on
// works on the first try instead of the next time the browser is restarted.
self.addEventListener('install', () => self.skipWaiting())
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()))

self.addEventListener('push', (event) => {
  let alert = {}
  try {
    alert = event.data ? event.data.json() : {}
  } catch {
    /* A payload we cannot read still deserves to wake somebody — see the fallbacks below. */
  }

  const urgent = alert.severity === 'Urgent'

  event.waitUntil(
    (async () => {
      await self.registration.showNotification(alert.title || 'Frontly', {
        body: alert.body || 'Something needs your attention.',
        icon: '/logo-mark.png',
        badge: '/logo-mark.png',
        // One tag per alert, so the escalation repeats replace the earlier notification instead
        // of stacking four copies of the same emergency on the lock screen.
        tag: `alert-${alert.id ?? Date.now()}`,
        // ...but a replacement should still buzz, otherwise chasing an ignored alert is silent
        // and the whole escalation is pointless.
        renotify: urgent,
        // The one that matters for an emergency: the notification stays on screen until it is
        // acted on, rather than sliding away while the phone is face down on a counter.
        requireInteraction: urgent,
        vibrate: urgent ? [200, 100, 200, 100, 200] : [120],
        timestamp: Date.now(),
        data: { url: alert.url || '/', id: alert.id },
      })

      // An open dashboard should update itself rather than wait out its refetch interval. This
      // is why there is no separate websocket: the push already arrived, so the same message
      // serves the closed-app and open-app cases.
      const clients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })
      for (const client of clients) client.postMessage({ type: 'frontly-alert', alert })
    })(),
  )
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const url = event.notification.data?.url || '/'

  event.waitUntil(
    (async () => {
      const clients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })

      // Focus a tab that is already open rather than piling up windows, and tell the app where
      // to go — client.navigate() would reload the whole bundle for what is a route change.
      const existing = clients.find((client) => 'focus' in client)
      if (existing) {
        await existing.focus()
        existing.postMessage({ type: 'frontly-navigate', url })
        return
      }

      await self.clients.openWindow(url)
    })(),
  )
})
