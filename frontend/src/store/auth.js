import { create } from 'zustand'
import { persist } from 'zustand/middleware'

/**
 * Session state.
 *
 * Nothing sensitive is written to localStorage: the refresh token lives in an httpOnly
 * cookie the page cannot read, and the short-lived access token is kept in memory only.
 * Only the display identity is persisted, so a reload can render the shell immediately
 * while the access token is restored by the silent refresh in App.jsx.
 */
export const useAuthStore = create(
  persist(
    (set) => ({
      user: null,
      accessToken: null,
      /** False until the silent refresh on start-up has settled. */
      ready: false,
      setSession: (user, accessToken) => set({ user, accessToken }),
      setAccessToken: (accessToken) => set({ accessToken }),
      setReady: (ready) => set({ ready }),
      logout: () => set({ user: null, accessToken: null }),
    }),
    {
      name: 'ai-receptionist-auth',
      partialize: (state) => ({ user: state.user }),
    },
  ),
)
