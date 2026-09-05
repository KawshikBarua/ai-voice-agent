import { useQuery } from '@tanstack/react-query'
import { api, unwrap } from './client'

/**
 * Minutes, live.
 *
 * Its own endpoint, deliberately small: the billing summary closes elapsed periods and reads
 * invoice history, which is far too much to run on a timer. This is the figure that moves while
 * calls are landing, so it is the only one polled — and the server measures it over the same usage
 * window the invoice is computed from, so what a customer watches is the balance they are billed
 * against, not a second opinion about it.
 *
 * Shared rather than owned by a page: the dashboard tile and the billing page must never show two
 * different numbers, and the surest way to guarantee that is one query key for both.
 */
export function useUsage() {
  return useQuery({
    queryKey: ['billing-usage'],
    queryFn: () => api.get('/billing/usage').then(unwrap),
    // A call has to end before its minutes are logged, so there is nothing finer-grained to catch
    // than this; polling harder would only spend requests. Focus covers the tab left open all day.
    refetchInterval: 15_000,
    refetchOnWindowFocus: true,
    staleTime: 0,
  })
}
