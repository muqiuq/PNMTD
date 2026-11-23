<script setup lang="ts">
import AutoComplete from 'primevue/autocomplete'
import { computed, ref } from 'vue'
import { kbLinkIcon, kbNumber, type KbLinkKind } from '@/api/kb'
import type { Schemas } from '@/api/client'
import { api } from '@/api/resources'

/**
 * One picker for what a page links to (docs/locations-knowledge-plan.md §6.5): locations, hosts, sensors and other pages
 * (docs/knowledge-base-extensions-plan.md §7) through the global search. `kinds` narrows it, as the list's filter does to the
 * objects. Picking one emits it and clears the field; the list's filter, the page's panels and the editing page use it.
 * `search` replaces the global search: a responsibility's links find their objects without Monitoring: View
 * (docs/responsibilities-plan.md §9.3).
 */
export interface LinkTarget {
  kind: KbLinkKind
  id: string
  name: string
  detail: string | null
  /** A page's number, for its route. */
  number?: number | null
  /** A linked page that is archived: its link can't change until it is restored. */
  archived?: boolean
}

const props = withDefaults(
  defineProps<{
    placeholder?: string
    exclude?: readonly { kind: KbLinkKind; id: string }[]
    kinds?: readonly KbLinkKind[]
    search?: (q: string) => Promise<Schemas['SearchResult']>
  }>(),
  {
    placeholder: undefined,
    exclude: () => [],
    kinds: () => ['Location', 'Host', 'Sensor', 'Page'],
    search: undefined,
  },
)
const what = computed(() => {
  const names = (['Page', 'Location', 'Host', 'Sensor'] as const)
    .filter((k) => props.kinds.includes(k))
    .map((k) => ({ Page: 'page', Location: 'location', Host: 'host', Sensor: 'sensor' })[k])
  return names.length > 1 ? `${names.slice(0, -1).join(', ')} or ${names[names.length - 1]}` : (names[0] ?? '')
})
const label = computed(() => `Link to a ${what.value}`)
const emit = defineEmits<{ pick: [target: LinkTarget] }>()

interface Suggestion extends LinkTarget {
  key: string
  icon: string
}

const text = ref<string | Suggestion>('')
const suggestions = ref<Suggestion[]>([])

async function complete(event: { query: string }) {
  const q = event.query.trim()
  if (!q || q.startsWith('#')) {
    suggestions.value = []
    return
  }
  const result = props.search ? await props.search(q) : await api.search(q, 8)
  const found: Suggestion[] = [
    ...(result.pages ?? []).map((p) => ({
      kind: 'Page' as const,
      id: p.id,
      name: p.title,
      detail: kbNumber(p.number),
      number: p.number,
    })),
    ...(result.locations ?? []).map((l) => ({ kind: 'Location' as const, id: l.id, name: l.name, detail: l.path })),
    ...result.hosts.map((h) => ({ kind: 'Host' as const, id: h.id, name: h.name, detail: h.location })),
    ...result.sensors.map((s) => ({ kind: 'Sensor' as const, id: s.id, name: s.name, detail: s.hostName })),
  ]
    .filter((t) => props.kinds.includes(t.kind))
    .map((t) => ({ ...t, key: `${t.kind}:${t.id}`, icon: kbLinkIcon[t.kind] }))
  suggestions.value = found
}

function select(event: { value: Suggestion }) {
  const { kind, id, name, detail, number } = event.value
  emit('pick', { kind, id, name, detail, number: number ?? null })
  text.value = ''
}
</script>

<template>
  <AutoComplete
    v-model="text"
    :suggestions="suggestions.filter((s) => !exclude?.some((e) => e.kind === s.kind && e.id === s.id))"
    option-label="name"
    :placeholder="placeholder ?? `${label}…`"
    :delay="200"
    size="small"
    fluid
    :aria-label="label"
    @complete="complete"
    @option-select="select"
  >
    <template #option="{ option }">
      <span class="option">
        <i :class="option.icon" class="muted" aria-hidden="true" />
        <span class="name">{{ option.name }}</span>
        <span v-if="option.detail" class="muted ellipsis">{{ option.detail }}</span>
      </span>
    </template>
  </AutoComplete>
</template>

<style scoped>
.option {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.name {
  font-weight: 500;
  white-space: nowrap;
}
</style>
