import { create } from 'zustand'
import { createJSONStorage, persist } from 'zustand/middleware'
import { queryClient } from '../lib/queryClient'
import { resetSession } from '../api/crypto'

/**
 * Session state.
 *
 * Nothing sensitive is written to localStorage: the refresh token lives in an httpOnly
 * cookie the page cannot read, and the short-lived access token is kept in memory only.
 * Only the display identity is persisted, so a reload can render the shell immediately
 * while the access token is restored by the silent refresh in App.jsx.
 */

/** Which account a user object belongs to. Null when signed out. */
const identityOf = (user) => (user ? `${user.organizationId}:${user.id}` : null)

/**
 * Everything the previous account left in memory. The query cache is the one that matters:
 * its keys carry no tenant, so a stale ['knowledge-base'] from the last account would be
 * handed straight to the next one. The payload encryption session goes too — the new user
 * should not inherit the old tab's AES key.
 */
const clearTenantState = () => {
  queryClient.clear()
  resetSession()
}

/**
 * Where the display identity is kept, following the "Keep me signed in" tick.
 *
 * The tick's real effect is on the refresh cookie (the server makes it a session cookie when it is
 * clear), and this mirrors it: an un-remembered sign-in must not leave a name and email in
 * localStorage for the next person to open the browser on a shared machine. sessionStorage is
 * emptied by the browser at the same moment the session cookie is.
 *
 * Reads try both, because the flag can change between visits and the value has to be found
 * wherever the last sign-in put it. Writes clear the other one for the same reason.
 */
const REMEMBER_KEY = 'ai-receptionist-remember'

const safely = (fn, fallback = null) => { try { return fn() } catch { return fallback } }

export const setRemember = (remember) =>
  safely(() => localStorage.setItem(REMEMBER_KEY, remember ? '1' : '0'))

const rememberChosen = () => safely(() => localStorage.getItem(REMEMBER_KEY) !== '0', true)

const identityStorage = {
  getItem: (name) =>
    safely(() => localStorage.getItem(name) ?? sessionStorage.getItem(name)),
  setItem: (name, value) => safely(() => {
    const [into, outOf] = rememberChosen() ? [localStorage, sessionStorage] : [sessionStorage, localStorage]
    into.setItem(name, value)
    outOf.removeItem(name)
  }),
  removeItem: (name) => safely(() => {
    localStorage.removeItem(name)
    sessionStorage.removeItem(name)
  }),
}

export const useAuthStore = create(
  persist(
    (set, get) => ({
      user: null,
      accessToken: null,
      /** False until the silent refresh on start-up has settled. */
      ready: false,
      /**
       * Clears the cache whenever the account actually changes, which covers signing in after
       * a logout that did not finish and switching accounts outright. A token refresh calls
       * this with the same user on every 401, so comparing identity keeps that a no-op —
       * clearing there would throw away the cache several times a session.
       */
      setSession: (user, accessToken) => {
        if (identityOf(get().user) !== identityOf(user)) clearTenantState()
        set({ user, accessToken })
      },
      setAccessToken: (accessToken) => set({ accessToken }),
      setReady: (ready) => set({ ready }),
      logout: () => {
        clearTenantState()
        set({ user: null, accessToken: null })
      },
    }),
    {
      name: 'ai-receptionist-auth',
      storage: createJSONStorage(() => identityStorage),
      partialize: (state) => ({ user: state.user }),
    },
  ),
)
