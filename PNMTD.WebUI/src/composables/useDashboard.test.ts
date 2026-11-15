import { QueryClient } from '@tanstack/vue-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, keys } from '@/api/resources'
import { refreshDashboard } from './useDashboard'

vi.mock('@/api/resources', async (original) => {
  const actual = await original<typeof import('@/api/resources')>()
  return { ...actual, api: { ...actual.api, dashboard: vi.fn() } }
})

const dashboard = vi.mocked(api.dashboard)
const built = (at: string) => ({ generatedAt: at }) as Awaited<ReturnType<typeof api.dashboard>>

describe('refreshDashboard', () => {
  beforeEach(() => dashboard.mockReset())

  it('asks for a fresh build, and a poll still in flight cannot overwrite it', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    let answerPoll: (value: ReturnType<typeof built>) => void = () => {}
    dashboard.mockImplementationOnce(() => new Promise((resolve) => (answerPoll = resolve)))
    const poll = queryClient.fetchQuery({ queryKey: keys.dashboard, queryFn: () => api.dashboard() }).then(
      () => 'answered',
      () => 'cancelled',
    )
    dashboard.mockResolvedValueOnce(built('2026-11-15T12:00:30Z'))

    await refreshDashboard(queryClient)
    answerPoll(built('2026-11-15T12:00:00Z'))
    await Promise.resolve()

    expect(await poll).toBe('cancelled')
    expect(dashboard).toHaveBeenLastCalledWith(true)
    expect(queryClient.getQueryData<ReturnType<typeof built>>(keys.dashboard)?.generatedAt).toBe('2026-11-15T12:00:30Z')
  })

  it('leaves a failure to the query, as a failed poll', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    queryClient.setQueryData(keys.dashboard, built('2026-11-15T12:00:00Z'))
    dashboard.mockRejectedValueOnce(new Error('offline'))

    await expect(refreshDashboard(queryClient)).resolves.toBeUndefined()

    const state = queryClient.getQueryState(keys.dashboard)
    expect(state?.status).toBe('error')
    expect(queryClient.getQueryData<ReturnType<typeof built>>(keys.dashboard)?.generatedAt).toBe('2026-11-15T12:00:00Z')
  })
})
