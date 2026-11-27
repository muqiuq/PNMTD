import { useQueryClient } from '@tanstack/vue-query'
import { nextTick } from 'vue'
import { ApiError } from '@/api/client'
import { licenseKeys, licensesApi, requestOf, type LicenseDetail, type LicenseRequest } from '@/api/licenses'
import { useActions } from '@/composables/useActions'
import { credentialWarning } from './item'

/**
 * One field of the item saved where it is shown (docs/licences-plan.md §8.7): the whole request with the current values and the one
 * changed (requestOf), the page's copy replaced and every licence query refreshed. A note or support contact answered 422
 * looks_like_credential asks first and is sent again acknowledged; declined, the editor stays open with the reason.
 */
export function useItemSave(item: () => LicenseDetail) {
  const queryClient = useQueryClient()
  const { ask } = useActions()

  async function save(patch: Partial<LicenseRequest>, field = 'note'): Promise<LicenseDetail> {
    const current = item()
    const body = requestOf(current, patch)
    let updated: LicenseDetail
    try {
      updated = await licensesApi.update(current.id, body)
    } catch (e) {
      if (!(e instanceof ApiError && e.code === 'looks_like_credential')) throw e
      if (!(await ask('This looks like a key', `${credentialWarning} Save anyway?`, 'Save anyway')))
        throw new ApiError(422, {
          code: 'looks_like_credential',
          detail: credentialWarning,
          errors: { [field]: [credentialWarning] },
        })
      updated = await licensesApi.update(current.id, { ...body, acknowledgeCredentials: true })
    }
    queryClient.setQueryData(licenseKeys.item(updated.number), updated)
    // The fields show the saved value once the page has it.
    await nextTick()
    await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
    return updated
  }

  return { save }
}
