<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import IconField from 'primevue/iconfield'
import InputIcon from 'primevue/inputicon'
import InputText from 'primevue/inputtext'
import ProgressSpinner from 'primevue/progressspinner'
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import { computed, ref, watch } from 'vue'
import { api, keys } from '@/api/resources'
import StatusBadge from '@/components/StatusBadge.vue'
import { useActions } from '@/composables/useActions'

/**
 * *Add hosts…* on a location's page (docs/locations-knowledge-plan.md §3.4): hosts found by name, location text or
 * location, picked and assigned at once (PUT /hosts/location). A host has one location: one elsewhere moves here.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ location: { id: string; name: string } }>()

const search = ref('')
const debounced = ref('')
let timer: ReturnType<typeof setTimeout> | undefined
watch(search, (value) => {
  clearTimeout(timer)
  timer = setTimeout(() => (debounced.value = value.trim()), 250)
})
const picked = ref<Record<string, boolean>>({})
watch(visible, (open) => open && ((search.value = ''), (debounced.value = ''), (picked.value = {})))

const hosts = useQuery({
  queryKey: computed(() => [...keys.hosts, 'pick', debounced.value]),
  queryFn: () => api.hosts.list({ search: debounced.value || undefined, pageSize: 50, sort: 'name' }),
  enabled: visible,
  placeholderData: keepPreviousData,
})
const items = computed(() =>
  (hosts.data.value?.items ?? []).filter((h) => h.assignedLocation?.id !== props.location.id),
)
const ids = computed(() => Object.keys(picked.value).filter((id) => picked.value[id]))

const { run, busy } = useActions()
async function add() {
  const changed = await run(() => api.hosts.setLocation({ ids: ids.value, locationId: props.location.id }), {
    failure: 'Not added',
    invalidate: [keys.locations, keys.hosts],
  })
  if (changed !== undefined) {
    visible.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="`Add hosts to ${location.name}`"
    modal
    :style="{ width: '34rem' }"
    :breakpoints="{ '599px': '95vw' }"
  >
    <IconField>
      <InputIcon class="pi pi-search" />
      <InputText v-model="search" placeholder="Search hosts" aria-label="Search hosts" fluid autofocus />
    </IconField>
    <div v-if="!hosts.data.value" class="center"><ProgressSpinner style="width: 28px; height: 28px" /></div>
    <ul v-else class="hosts">
      <li v-for="host in items" :key="host.id">
        <Checkbox v-model="picked[host.id]" binary :input-id="`add-${host.id}`" />
        <label :for="`add-${host.id}`">
          <StatusBadge :status="host.state" icon-only />
          <span class="strong">{{ host.name }}</span>
          <span class="muted ellipsis">{{ host.assignedLocation?.path ?? host.location ?? '' }}</span>
        </label>
      </li>
      <li v-if="!items.length" class="muted">No host matches.</li>
    </ul>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        :label="ids.length ? `Add ${ids.length} host${ids.length === 1 ? '' : 's'}` : 'Add'"
        :disabled="!ids.length"
        :loading="busy"
        @click="add"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}

.hosts {
  list-style: none;
  margin: var(--pnmt-space-2) 0 0;
  padding: 0;
  max-height: 50vh;
  overflow-y: auto;
}

.hosts li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-1) 0;
}

.hosts label {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
  flex: 1;
  cursor: pointer;
}

.strong {
  font-weight: 500;
  white-space: nowrap;
}
</style>
