<script setup lang="ts">
import AutoComplete from 'primevue/autocomplete'
import { ref } from 'vue'
import { kbLinkIcon, type KbLinkKind } from '@/api/kb'
import { api } from '@/api/resources'

/**
 * One picker for what a page links to (docs/locations-knowledge-plan.md §6.5): locations, hosts and sensors through the
 * global search. Picking one emits it and clears the field; the list's filter and the editing page's details use it.
 */
export interface LinkTarget {
  kind: KbLinkKind
  id: string
  name: string
  detail: string | null
}

defineProps<{ placeholder?: string; exclude?: readonly { kind: KbLinkKind; id: string }[] }>()
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
  const result = await api.search(q, 8)
  const found: Suggestion[] = [
    ...(result.locations ?? []).map((l) => ({ kind: 'Location' as const, id: l.id, name: l.name, detail: l.path })),
    ...result.hosts.map((h) => ({ kind: 'Host' as const, id: h.id, name: h.name, detail: h.location })),
    ...result.sensors.map((s) => ({ kind: 'Sensor' as const, id: s.id, name: s.name, detail: s.hostName })),
  ].map((t) => ({ ...t, key: `${t.kind}:${t.id}`, icon: kbLinkIcon[t.kind] }))
  suggestions.value = found
}

function select(event: { value: Suggestion }) {
  const { kind, id, name, detail } = event.value
  emit('pick', { kind, id, name, detail })
  text.value = ''
}
</script>

<template>
  <AutoComplete
    v-model="text"
    :suggestions="suggestions.filter((s) => !exclude?.some((e) => e.kind === s.kind && e.id === s.id))"
    option-label="name"
    :placeholder="placeholder ?? 'Link to a location, host or sensor…'"
    :delay="200"
    size="small"
    fluid
    aria-label="Link to a location, host or sensor"
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
