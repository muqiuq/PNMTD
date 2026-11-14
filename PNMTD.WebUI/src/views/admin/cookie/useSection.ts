import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, ref, watch, type Ref } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import { useNotify } from '@/composables/useNotify'

/** The sections of Admin → Cookie (docs/cookie-plan.md §9.3), by the names the API uses. */
export type Section = keyof S['CookieAllSettings']

/** A section's values with every field present, as the server returns them. */
export type Values<K extends Section> = Required<S['CookieAllSettings'][K]>

export const settingsKey = [...keys.cookie, 'admin', 'settings'] as const

/** Every section as in force, one call for all tabs. */
export function useCookieSettings() {
  return useQuery({ queryKey: settingsKey, queryFn: api.cookie.settings })
}

/** The default prompts, the kinds of case and the limits' defaults and ceilings: fixed until a new release. */
export function useCookieDefaults() {
  return useQuery({
    queryKey: [...keys.cookie, 'admin', 'defaults'],
    queryFn: api.cookie.defaults,
    staleTime: Infinity,
  })
}

/** Redmine's projects, roles, statuses and trackers, as PNMT shows them; asked of Redmine, so only when needed. */
export function useRedmineLookups(enabled: Ref<boolean> | boolean = true) {
  return useQuery({
    queryKey: [...keys.cookie, 'admin', 'redmine'],
    queryFn: api.cookie.redmine,
    staleTime: 60_000,
    enabled,
  })
}

const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T

/**
 * One section of Admin → Cookie as a draft: loaded from the settings in force, edited in place, saved as a whole (a new
 * version) with the server's messages by field on a 400. A refetch replaces the draft only while it is unchanged, so a
 * refetch never throws away an edit; after a rollback it is replaced in any case.
 */
export function useSection<K extends Section>(
  section: K,
  options: {
    /** Brings the loaded values into the draft's shape (every weekday, Monday first). */
    normalize?: (value: Values<K>) => Values<K>
    /** What is sent: trimmed, empty texts as null. */
    prepare?: (value: Values<K>) => Values<K>
  } = {},
) {
  const queryClient = useQueryClient()
  const notify = useNotify()
  const settings = useCookieSettings()
  const draft = ref(null) as Ref<Values<K> | null>
  const base = ref('')
  const busy = ref(false)
  const errors = ref<Record<string, string[]>>({})

  function load(value: Values<K>) {
    const next = options.normalize ? options.normalize(clone(value)) : clone(value)
    draft.value = next
    base.value = JSON.stringify(next)
  }

  watch(
    () => settings.data.value?.[section] as Values<K> | undefined,
    (value) => {
      if (value && (draft.value === null || JSON.stringify(draft.value) === base.value)) load(value)
    },
    { immediate: true },
  )

  const dirty = computed(() => draft.value !== null && JSON.stringify(draft.value) !== base.value)

  async function save() {
    if (!draft.value || !dirty.value) return
    busy.value = true
    errors.value = {}
    try {
      const body = options.prepare ? options.prepare(clone(draft.value)) : draft.value
      const send = api.cookie.save[section] as (value: Values<K>) => Promise<S['CookieSaved']>
      const saved = await send(body)
      base.value = JSON.stringify(draft.value)
      notify.success('Saved', `Version ${saved.version}.`)
      await queryClient.invalidateQueries({ queryKey: keys.cookie })
    } catch (e) {
      if (e instanceof ApiError && Object.keys(e.errors).length) {
        errors.value = e.errors
      } else {
        notify.error(e, 'Not saved')
      }
    } finally {
      busy.value = false
    }
  }

  /** Back to the values in force. */
  function reset() {
    if (base.value) draft.value = JSON.parse(base.value) as Values<K>
    errors.value = {}
  }

  /** After a rollback: the draft becomes the new version, whatever was edited. */
  async function reload() {
    errors.value = {}
    await queryClient.invalidateQueries({ queryKey: settingsKey })
    const value = settings.data.value?.[section] as Values<K> | undefined
    if (value) load(value)
  }

  /** The first message for a field (its JSON name, or "week.Monday", "contacts.<id>", …). */
  const error = (field: string) => errors.value[field]?.[0]

  return { section, settings, draft, dirty, busy, errors, error, save, reset, reload }
}

/** "" and whitespace as null, the rest trimmed. */
export const textOrNull = (value: string | null | undefined) => (value?.trim() ? value.trim() : null)
