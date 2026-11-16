import { useQuery } from '@tanstack/vue-query'
import { computed, ref, type Ref } from 'vue'
import { api, keys, type S } from '@/api/resources'

export interface PickerOption {
  label: string
  value: string
  hint?: string
}

/** "e-mail, Pushover", or what is missing: the picker says it before the server refuses a user nobody can reach. */
export function contactHint(u: S['NotificationPerson']): string {
  const has = [u.hasEmail ? 'e-mail' : null, u.hasPushover ? 'Pushover' : null].filter((x) => x !== null)
  return has.length ? has.join(', ') : 'no contact details'
}

/**
 * What the rule editor chooses a user or a group from (docs/notification-recipients-plan.md §6.3, §7.1): PNMT's users with
 * whether they can be reached, never their details, and the groups with their size. Monitoring's Edit; loaded when wanted.
 */
export function useRecipientPickers(when: Ref<boolean> = ref(true)) {
  const query = useQuery({
    queryKey: [...keys.rules, 'people'],
    queryFn: api.rules.people,
    staleTime: 60_000,
    enabled: when,
  })
  const users = computed<PickerOption[]>(() =>
    (query.data.value?.users ?? []).map((u) => ({
      label: `${u.displayName || u.username}${u.isActive ? '' : ' (deactivated)'}`,
      value: u.id,
      hint: contactHint(u),
    })),
  )
  const groups = computed<PickerOption[]>(() =>
    (query.data.value?.groups ?? []).map((g) => ({
      label: g.name,
      value: g.id,
      hint: `${g.members} member${g.members === 1 ? '' : 's'}`,
    })),
  )
  return { query, users, groups }
}
