/**
 * Payload encryption for calls to the API.
 *
 * The browser generates an AES-256-GCM key, wraps it with the API's RSA public key and trades it
 * for a session id. From then on every request body goes up as `{ enc: "<base64>" }` and every
 * JSON response comes back the same way; `client.js` does the wrapping so no page or query has to
 * think about it.
 *
 * Worth being honest about what this buys: the key is in the page, so script running here can read
 * it. It keeps payloads out of proxy logs, devtools and disk caches — TLS is still what protects
 * the connection, and the server keeps its own authentication.
 *
 * Endpoints that machines call (the Retell webhook and live-call tools, the platform endpoints)
 * are exempt on the server and never see this.
 */

const HANDSHAKE_URL = '/api/v1/crypto/handshake'
const SESSION_URL = '/api/v1/crypto/session'

export const SESSION_HEADER = 'X-Enc-Session'
export const ENCRYPTED_HEADER = 'x-enc'
export const RENEW_HEADER = 'x-enc-renew'

/** Paths under /api/v1 the client must never wrap — they mirror the server's exempt list. */
const EXEMPT = ['/crypto', '/webhooks', '/ai/tools', '/platform']

export const isExempt = (url = '') => EXEMPT.some((p) => url.startsWith(p))

/**
 * Off in two cases: a browser without WebCrypto (any non-secure origin that isn't localhost),
 * and an explicit build-time opt-out. Both fall back to plain JSON rather than breaking the app,
 * which is also why the server's `Encryption:Required` is what makes this mandatory.
 */
export const encryptionAvailable = () =>
  import.meta.env.VITE_DISABLE_PAYLOAD_ENCRYPTION !== 'true' &&
  typeof globalThis.crypto?.subtle?.encrypt === 'function'

const b64 = (bytes) => {
  let binary = ''
  const view = new Uint8Array(bytes)
  // Chunked: String.fromCharCode(...) on a long array blows the argument limit.
  for (let i = 0; i < view.length; i += 0x8000) {
    binary += String.fromCharCode.apply(null, view.subarray(i, i + 0x8000))
  }
  return btoa(binary)
}

const unb64 = (text) => {
  const binary = atob(text)
  const out = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i)
  return out
}

let session = null // { id, key }
let pending = null // single-flight handshake

/**
 * One handshake at a time. Every queued request awaits the same promise, so a cold page load
 * firing five queries at once exchanges one key rather than five.
 */
async function handshake() {
  const keyResponse = await fetch(HANDSHAKE_URL, { headers: { Accept: 'application/json' } })
  if (!keyResponse.ok) throw new Error(`Handshake failed (${keyResponse.status})`)
  const { data } = await keyResponse.json()

  const publicKey = await crypto.subtle.importKey(
    'spki',
    unb64(data.publicKey),
    { name: 'RSA-OAEP', hash: 'SHA-256' },
    false,
    ['encrypt'],
  )

  const aesKey = await crypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, true, ['encrypt', 'decrypt'])
  const rawKey = await crypto.subtle.exportKey('raw', aesKey)
  const wrappedKey = b64(await crypto.subtle.encrypt({ name: 'RSA-OAEP' }, publicKey, rawKey))

  const sessionResponse = await fetch(SESSION_URL, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify({ wrappedKey }),
  })
  if (!sessionResponse.ok) throw new Error(`Key exchange failed (${sessionResponse.status})`)
  const opened = await sessionResponse.json()

  return { id: opened.data.sessionId, key: aesKey }
}

export async function getSession() {
  if (session) return session
  pending ??= handshake()
    .then((s) => { session = s; return s })
    .finally(() => { pending = null })
  return pending
}

/** Drops the current session so the next call negotiates a new one. */
export const resetSession = () => { session = null }

export async function sealBody(plaintext, { key }) {
  const iv = crypto.getRandomValues(new Uint8Array(12))
  const encoded = new TextEncoder().encode(plaintext)
  const sealed = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, key, encoded))

  // iv || ciphertext || tag — WebCrypto already appends the tag to the ciphertext.
  const packed = new Uint8Array(iv.length + sealed.length)
  packed.set(iv, 0)
  packed.set(sealed, iv.length)
  return { enc: b64(packed) }
}

export async function openBody(envelope, { key }) {
  const packed = unb64(envelope)
  const iv = packed.subarray(0, 12)
  const body = packed.subarray(12)
  const plaintext = await crypto.subtle.decrypt({ name: 'AES-GCM', iv }, key, body)
  return new TextDecoder().decode(plaintext)
}
