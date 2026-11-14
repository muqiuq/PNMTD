<script setup lang="ts">
import AutoComplete from 'primevue/autocomplete'
import { ref } from 'vue'
import { api, type RedmineTarget } from '@/api/resources'

/**
 * What a ticket is linked to (docs/audit-redmine-zabbix-plan.md §6.5): a knowledge base page, a location, a host or a
 * sensor, found through the global search. Picking one emits it.
 */
export interface Target {
  key: keyof RedmineTarget
  id: string
  name: string
  detail: string | null
  icon: string
}

const emit = defineEmits<{ pick: [target: Target] }>()
const text = ref<string | Target>('')
const suggestions = ref<Target[]>([])

async function complete(event: { query: string }) {
  const q = event.query.trim()
  if (!q) {
    suggestions.value = []
    return
  }
  const r = await api.search(q, 8)
  suggestions.value = [
    ...r.pages.map((p) => ({
      key: 'pageId' as const,
      id: p.id,
      name: `KB-${p.number} ${p.title}`,
      detail: p.summary,
      icon: 'pi pi-book',
    })),
    ...(r.locations ?? []).map((l) => ({
      key: 'locationId' as const,
      id: l.id,
      name: l.name,
      detail: l.path,
      icon: 'pi pi-map-marker',
    })),
    ...r.hosts.map((h) => ({
      key: 'hostId' as const,
      id: h.id,
      name: h.name,
      detail: h.location,
      icon: 'pi pi-server',
    })),
    ...r.sensors.map((s) => ({
      key: 'sensorId' as const,
      id: s.id,
      name: s.name,
      detail: s.hostName,
      icon: 'pi pi-wave-pulse',
    })),
  ]
}

function select(event: { value: Target }) {
  emit('pick', event.value)
  text.value = ''
}
</script>

<template>
  <AutoComplete
    v-model="text"
    :suggestions="suggestions"
    option-label="name"
    placeholder="A page, location, host or sensor…"
    :delay="200"
    size="small"
    fluid
    aria-label="Link the ticket to"
    @complete="complete"
    @option-select="select"
  >
    <template #option="{ option }">
      <span class="option">
        <i :class="option.icon" class="muted" aria-hidden="true" />
        <span>{{ option.name }}</span>
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
</style>
