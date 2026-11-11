import { QueryClient } from '@tanstack/vue-query'
import { ApiError } from './client'

/**
 * Server state (§5.1, §5.7): lists and the dashboard refetch every 30 s and when the window regains
 * focus; a client error (4xx) is not retried, a lost connection twice.
 */
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 10_000,
      refetchOnWindowFocus: true,
      retry: (count, error) => !(error instanceof ApiError && error.status >= 400 && error.status < 500) && count < 2,
    },
    mutations: { retry: false },
  },
})
