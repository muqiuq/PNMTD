import { useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'

/** What a host's or a sensor's page shows of Cookie's notes (docs/cookie-object-notes-plan.md §6). */
export type CookieNoteObject = 'host' | 'sensor'

/**
 * Cookie's notes on a host or a sensor: the list the reader may see (the server filters by the note's areas), whether the
 * reader may remove one (Monitoring: Edit) or follow its case (Cookie: Use), and removing with a confirm.
 */
export function useCookieNotes(type: CookieNoteObject, id: MaybeRefOrGetter<string>) {
  const session = useSession()
  const { ask, done } = useActions()
  const queryKey = computed(() => keys.cookieNotes(type, toValue(id)))
  const query = useQuery({
    queryKey,
    queryFn: () => (type === 'host' ? api.hosts.cookieNotes(toValue(id)) : api.sensors.cookieNotes(toValue(id))),
  })
  const notes = computed(() => query.data.value ?? [])
  const canRemove = computed(() => session.can('Monitoring', 'Edit'))
  const canLinkCase = computed(() => session.can('Cookie', 'Use'))

  async function remove(note: S['CookieObjectNoteItem']) {
    const when = new Date(note.at).toLocaleDateString(undefined, { day: 'numeric', month: 'short' })
    if (await ask(`Remove Cookie's note of ${when}?`, 'The note is gone from this page; the decision log keeps it.')) {
      await done(() => api.cookie.removeObjectNote(note.id), { success: 'Note removed', invalidate: [queryKey.value] })
    }
  }

  return { notes, canRemove, canLinkCase, remove }
}
