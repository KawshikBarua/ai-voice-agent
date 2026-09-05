import axios from 'axios'
import { useAuthStore } from '../store/auth'
import {
  ENCRYPTED_HEADER, RENEW_HEADER, SESSION_HEADER,
  encryptionAvailable, getSession, isExempt, openBody, resetSession, sealBody,
} from './crypto'

// withCredentials so the httpOnly refresh cookie rides along on the auth calls.
export const api = axios.create({
  baseURL: '/api/v1',
  withCredentials: true,
})

api.interceptors.request.use((config) => {
  const token = useAuthStore.getState().accessToken
  if (token) config.headers.Authorization = `Bearer ${token}`
  return config
})

/**
 * Payload encryption (see crypto.js). Applied to every call the web app makes, so it is fitted
 * here rather than at each call site — pages keep passing and reading plain objects.
 *
 * `transformRequest` is disabled for sealed requests: axios would otherwise re-serialize the
 * envelope, and the body is already the exact JSON string we want on the wire.
 */
api.interceptors.request.use(async (config) => {
  if (!encryptionAvailable() || isExempt(config.url ?? '') || config.skipEncryption) return config

  const session = await getSession()
  config.headers[SESSION_HEADER] = session.id

  if (config.data !== undefined && config.data !== null && !(config.data instanceof FormData)) {
    const plaintext = typeof config.data === 'string' ? config.data : JSON.stringify(config.data)
    config.data = JSON.stringify(await sealBody(plaintext, session))
    config.headers['Content-Type'] = 'application/json'
    config.transformRequest = [(body) => body]
  }
  return config
})

/** Unseals a response in place when the server marked it encrypted. */
async function openResponse(response) {
  if (response?.headers?.[ENCRYPTED_HEADER] !== '1') return response
  const envelope = response.data?.enc
  if (typeof envelope !== 'string') return response

  const session = await getSession()
  response.data = JSON.parse(await openBody(envelope, session))
  return response
}

let refreshing = null

/**
 * Rotates the session using the httpOnly cookie. No token is passed from JavaScript.
 *
 * Single-flight across the whole app: refresh ROTATES the token, and the server treats a
 * replayed token as theft and revokes the entire family. Two concurrent refreshes carrying
 * the same cookie would therefore log the user out. Every caller — the 401 interceptor and
 * the start-up bootstrap (which React StrictMode invokes twice in dev) — shares one request.
 *
 * It goes through `api` so the payload is encrypted like everything else; `baseURL` already
 * covers the /api/v1 prefix.
 */
export const requestRefresh = () => {
  refreshing ??= api
    // _isRefresh keeps the 401 handler below off this call: a rejected refresh triggering
    // another refresh would loop until the browser gave up.
    .post('/auth/refresh', {}, { _isRefresh: true })
    .finally(() => { refreshing = null })
  return refreshing
}

api.interceptors.response.use(
  (res) => openResponse(res),
  async (error) => {
    const original = error.config

    // The encryption session outlived its server-side entry (a restart, or an idle tab past the
    // expiry). Nothing is wrong with the user's credentials — negotiate a new one and retry once.
    if (error.response?.headers?.[RENEW_HEADER] === '1' && original && !original._encRetried) {
      original._encRetried = true
      resetSession()
      return api(original)
    }

    if (error.response?.status === 401 && original && !original._retried && !original._isRefresh) {
      original._retried = true
      try {
        const { data } = await requestRefresh()
        useAuthStore.getState().setSession(data.data.user, data.data.accessToken)
        original.headers.Authorization = `Bearer ${data.data.accessToken}`
        return api(original)
      } catch (refreshError) {
        useAuthStore.getState().logout()
        return Promise.reject(refreshError)
      }
    }

    // Failures are answered in the clear (the client may not hold a usable key at that point),
    // but a sealed error body still has to be opened before callers read `message`.
    if (error.response) {
      try { await openResponse(error.response) } catch { /* leave the raw body in place */ }
    }
    return Promise.reject(error)
  },
)

/**
 * Signs out for real: revokes the refresh token server-side and clears its cookie, then drops
 * every trace of the account from this tab.
 *
 * Clearing local state alone is not a sign-out — the refresh cookie survives it, so the next
 * reload trades it for a fresh token and silently signs the same user back in.
 *
 * The local half runs even when the call fails (offline, expired token). Leaving someone
 * apparently signed in because the network was down is the worse outcome; the token they keep
 * is one the server will still honour, which is why the request is attempted first.
 */
export const signOut = async () => {
  try {
    await api.post('/auth/logout', {})
  } catch {
    /* already expired, or offline — the local clear below still has to happen */
  } finally {
    useAuthStore.getState().logout()
  }
}

/** Unwraps the standardized ApiResponse envelope. */
export const unwrap = (res) => res.data.data
