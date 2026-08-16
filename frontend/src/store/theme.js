import { create } from 'zustand'
import { persist } from 'zustand/middleware'

export const STORAGE_KEY = 'frontly-theme'

const prefersDark = () =>
  typeof window !== 'undefined' &&
  window.matchMedia?.('(prefers-color-scheme: dark)').matches

/** 'system' follows the OS; 'light' / 'dark' pin it regardless. */
export const resolveTheme = (mode) =>
  mode === 'system' ? (prefersDark() ? 'dark' : 'light') : mode

/** The single place the class is written — index.html's pre-paint script mirrors this. */
export const applyTheme = (resolved) => {
  document.documentElement.classList.toggle('dark', resolved === 'dark')
}

export const useThemeStore = create(
  persist(
    (set, get) => ({
      mode: 'system',
      setMode: (mode) => {
        set({ mode })
        applyTheme(resolveTheme(mode))
      },
      /** The theme actually on screen right now, which 'system' alone does not tell you. */
      resolved: () => resolveTheme(get().mode),
    }),
    {
      name: STORAGE_KEY,
      partialize: (state) => ({ mode: state.mode }),
      // Storage is read before React mounts (see index.html), but a rehydrate from another
      // tab still has to repaint this one.
      onRehydrateStorage: () => (state) => {
        if (state) applyTheme(resolveTheme(state.mode))
      },
    },
  ),
)

/**
 * Keeps 'system' honest: without this the page would keep whatever the OS was set to at
 * load time even after the user switches their OS to dark.
 */
export const watchSystemTheme = () => {
  const query = window.matchMedia?.('(prefers-color-scheme: dark)')
  if (!query) return () => {}

  const onChange = () => {
    if (useThemeStore.getState().mode === 'system') applyTheme(resolveTheme('system'))
  }
  query.addEventListener('change', onChange)
  return () => query.removeEventListener('change', onChange)
}
