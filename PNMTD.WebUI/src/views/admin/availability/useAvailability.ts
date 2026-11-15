import { useQuery } from '@tanstack/vue-query'
import { computed, type Ref } from 'vue'
import { api, keys } from '@/api/resources'

/** The availability plan's queries (docs/cookie-plan.md §8.2, §9.4), shared by its tabs, Account and Admin → Users. */

export const planKey = [...keys.availability, 'plan'] as const
export const settingsKey = [...keys.availability, 'settings'] as const
export const peopleKey = [...keys.availability, 'people'] as const
export const contactKey = [...keys.account, 'contact'] as const

/** One's own contact details, and whether one is in the plan (which shows the plan without a right). */
export function useContact(enabled: Ref<boolean> | boolean = true) {
  return useQuery({ queryKey: contactKey, queryFn: api.account.contact, staleTime: 5 * 60_000, enabled })
}

/** The plan's settings with every type, retired ones too, and the groups to pick from (Availability: Manage, admins). */
export function useAvailabilitySettings(enabled: Ref<boolean> | boolean = true) {
  return useQuery({ queryKey: settingsKey, queryFn: api.availability.settings, staleTime: 60_000, enabled })
}

/** The types one can choose, in their order: for a person's default week. */
export function useTypeOptions(enabled: Ref<boolean> | boolean = true) {
  const settings = useAvailabilitySettings(enabled)
  const types = computed(() =>
    (settings.data.value?.types ?? []).filter((t) => !t.retired).sort((a, b) => a.order - b.order),
  )
  return { settings, types }
}

/** Every active user with their place in the plan (Availability: Manage). */
export function useAvailabilityPeople(enabled: Ref<boolean> | boolean = true) {
  return useQuery({ queryKey: peopleKey, queryFn: api.availability.people, staleTime: 30_000, enabled })
}
