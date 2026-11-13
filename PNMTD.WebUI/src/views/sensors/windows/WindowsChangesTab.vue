<script setup lang="ts">
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'

/** What changed on the server (docs/windows-sensor-plan.md §4.9), newest first; notable changes wrote 253. */
const props = defineProps<{ server: S['WindowsServerDetail'] }>()
const sectionName = ref<string | null>(null)
const notableOnly = ref(false)
const filters = computed(() => ({
  section: sectionName.value ?? undefined,
  notable: notableOnly.value ? true : undefined,
}))
const table = useServerTable<S['WindowsChangeItem']>({
  key: [...keys.windowsServer(props.server.server.sensorId), 'changes'],
  fetch: (q) => api.windows.changes(props.server.server.sensorId, q),
  pageSize: 50,
  filters,
})
const sections = ['system', 'hardware', 'network', 'services', 'remote-access', 'access']
</script>

<template>
  <div class="tab">
    <div class="toolbar">
      <Select v-model="sectionName" :options="sections" show-clear size="small" placeholder="Every section" />
      <label class="check"><input v-model="notableOnly" type="checkbox" /> notable only</label>
    </div>
    <ListTable
      :table="table"
      what="changes"
      empty-text="No change seen yet: the first report of each section is the baseline."
    >
      <Column header="When" style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.at" /></template>
      </Column>
      <Column header="Section" style="width: 9rem">
        <template #body="{ data }"
          ><span class="muted">{{ data.section }}</span></template
        >
      </Column>
      <Column header="Change">
        <template #body="{ data }">
          {{ data.summary }} <Tag v-if="data.notable" value="253" severity="info" class="small" />
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="small muted"><RelativeTime :value="item.at" /> · {{ item.section }}</div>
        <div>{{ item.summary }}</div>
      </template>
    </ListTable>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.toolbar,
.check {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: center;
}

.small {
  font-size: 0.857rem;
}
</style>
