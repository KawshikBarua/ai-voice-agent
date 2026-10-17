import { useQuery } from '@tanstack/react-query'
import { api, unwrap } from '../api/client'

/**
 * The business's timezone as an IANA id, for showing times on its clock rather than the
 * browser's. Shares the ['organization'] cache entry with Settings, so it costs no extra request
 * once either has loaded. Undefined until then, which formats on the browser's clock for a moment.
 */
export function useBusinessTimeZone() {
  const { data } = useQuery({
    queryKey: ['organization'],
    queryFn: () => api.get('/settings/organization').then(unwrap),
    staleTime: 5 * 60_000,
  })
  return data?.timezoneIana || data?.timezone || undefined
}
