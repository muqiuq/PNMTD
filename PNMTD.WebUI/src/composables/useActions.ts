import { useQueryClient } from '@tanstack/vue-query'
import { useConfirm } from 'primevue/useconfirm'
import { ref } from 'vue'
import { useNotify } from './useNotify'

/**
 * Changes from a page: a toast on success, the affected queries refreshed (the dashboard always),
 * a toast on failure (§5.7). Destructive ones ask first, naming what goes with them.
 */
export function useActions() {
  const queryClient = useQueryClient()
  const notify = useNotify()
  const confirm = useConfirm()
  const busy = ref(false)

  async function invalidate(prefixes: readonly (readonly unknown[])[]) {
    await Promise.all(
      [...prefixes, ['dashboard']].map((queryKey) => queryClient.invalidateQueries({ queryKey: [...queryKey] })),
    )
  }

  async function run<T>(
    action: () => Promise<T>,
    options: { success?: string; failure?: string; invalidate?: readonly (readonly unknown[])[] } = {},
  ): Promise<T | undefined> {
    busy.value = true
    try {
      const result = await action()
      if (options.success) {
        notify.success(options.success)
      }
      await invalidate(options.invalidate ?? [])
      return result
    } catch (e) {
      notify.error(e, options.failure ?? 'Not done')
      return undefined
    } finally {
      busy.value = false
    }
  }

  /** As run, for actions without a result (a DELETE answers 204): whether it worked. */
  async function done(
    action: () => Promise<unknown>,
    options: { success?: string; failure?: string; invalidate?: readonly (readonly unknown[])[] } = {},
  ): Promise<boolean> {
    let ok = false
    await run(async () => {
      await action()
      ok = true
    }, options)
    return ok
  }

  /** Asks with a danger button; resolves true when confirmed. */
  function ask(header: string, message: string, accept = 'Delete'): Promise<boolean> {
    return new Promise((resolve) =>
      confirm.require({
        header,
        message,
        icon: 'pi pi-exclamation-triangle',
        acceptProps: { label: accept, severity: 'danger' },
        rejectProps: { label: 'Cancel', severity: 'secondary', text: true },
        accept: () => resolve(true),
        reject: () => resolve(false),
        onHide: () => resolve(false),
      }),
    )
  }

  return { busy, run, done, ask, invalidate, notify }
}
