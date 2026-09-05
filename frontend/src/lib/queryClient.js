import { QueryClient } from '@tanstack/react-query'

/**
 * The one query cache for the app.
 *
 * It lives here rather than in main.jsx because signing out has to be able to empty it. The
 * cache is keyed by resource name alone — ['knowledge-base'], ['customers'] — with no tenant
 * in the key, so anything left in it when a different account signs in would be served to that
 * account as its own data. The auth store clears it on every identity change; see store/auth.js.
 */
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: 1, refetchOnWindowFocus: false, staleTime: 30_000 },
  },
})
