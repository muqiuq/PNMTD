import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'

/**
 * A filter kept in the URL's query (?filter=weak), so the strip's figures and links open it: read from the route, written by
 * replacing the route. The list's own URL sync (useServerTable) copies the query as it is, so the two never race.
 */
export function useQueryParam<T extends string>(name: string) {
  const route = useRoute()
  const router = useRouter()
  return computed<T | null>({
    get: () => (typeof route.query[name] === 'string' && route.query[name] ? (route.query[name] as T) : null),
    set: (value) => void router.replace({ query: { ...route.query, [name]: value ?? undefined, page: undefined } }),
  })
}
