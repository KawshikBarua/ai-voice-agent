import { useEffect, useRef, useState, useSyncExternalStore } from 'react'

/** Width from which a list is shown as a table rather than as cards. */
export const DESKTOP = '(min-width: 768px)'

/** Tracks a media query, so only one of the two layouts is ever mounted — rendering both and
 *  hiding one with CSS would double every row in the DOM. */
export function useMediaQuery(query) {
  return useSyncExternalStore(
    (onChange) => {
      const mq = window.matchMedia(query)
      mq.addEventListener('change', onChange)
      return () => mq.removeEventListener('change', onChange)
    },
    () => window.matchMedia(query).matches,
  )
}

/** Holds a value back until it has stopped changing, so typing sends one search, not one per key. */
export function useDebounced(value, ms = 300) {
  const [settled, setSettled] = useState(value)
  useEffect(() => {
    const t = setTimeout(() => setSettled(value), ms)
    return () => clearTimeout(t)
  }, [value, ms])
  return settled
}

/**
 * Infinite scroll for a useInfiniteQuery list.
 *
 * The next page is requested when the sentinel comes within reach of the bottom of whatever is
 * scrolling — the element on `scrollerRef` when `contained`, the page itself otherwise.
 * Rebuilding the observer whenever `count` changes re-checks a sentinel that is still in view,
 * so a tall screen keeps filling until it is covered.
 */
export function useLoadMore({ hasNextPage, isFetchingNextPage, fetchNextPage }, { contained, count }) {
  const scrollerRef = useRef(null)
  const sentinelRef = useRef(null)
  useEffect(() => {
    const el = sentinelRef.current
    if (!el || !hasNextPage || isFetchingNextPage) return
    const io = new IntersectionObserver(
      ([entry]) => { if (entry.isIntersecting) fetchNextPage() },
      { root: contained ? scrollerRef.current : null, rootMargin: '400px' },
    )
    io.observe(el)
    return () => io.disconnect()
  }, [hasNextPage, isFetchingNextPage, fetchNextPage, contained, count])
  return { scrollerRef, sentinelRef }
}
