<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import InputText from 'primevue/inputtext'
import Tag from 'primevue/tag'
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import { useSession } from '@/stores/session'
import AddCallerDialog from './AddCallerDialog.vue'

/** *Callers* (docs/connectors-plan.md §13.4): the outside systems, each with one key, its networks and the connectors granted to it. */
const session = useSession()
const router = useRouter()
const table = useServerTable<S['ConnectorCallerItem']>({
  key: [...keys.connectors, 'callers'],
  fetch: (q) => api.connectors.callers(q),
  pageSize: 50,
  syncUrl: true,
})
const adding = ref(false)
</script>

<template>
  <div class="tab">
    <div class="toolbar">
      <InputText v-model="table.search.value" placeholder="Search" aria-label="Search callers" size="small" />
      <span class="muted small">One key per caller: it opens the connectors granted to it, from its networks.</span>
      <Button v-if="session.can('Connectors', 'Manage')" label="Add caller…" icon="pi pi-plus" @click="adding = true" />
    </div>
    <ListTable
      :table="table"
      what="callers"
      selectable
      empty-text="No caller yet."
      @select="(c) => router.push({ name: 'connector-caller', params: { id: c.id } })"
    >
      <Column header="Caller">
        <template #body="{ data }">
          <strong>{{ data.name }}</strong>
          <div v-if="data.description" class="small muted">{{ data.description }}</div>
        </template>
      </Column>
      <Column header="Key" style="width: 7rem">
        <template #body="{ data }"
          ><code class="muted">…{{ data.keyHint }}</code></template
        >
      </Column>
      <Column header="Networks" style="width: 10rem">
        <template #body="{ data }">
          {{ data.networks }}
          <Tag v-if="data.anyAddress" value="any address" severity="warn" class="small-tag" />
        </template>
      </Column>
      <Column header="Connectors">
        <template #body="{ data }"
          ><span class="small">{{ data.connectors.map((c: S['Ref']) => c.name).join(', ') || 'none' }}</span></template
        >
      </Column>
      <Column header="Last call · 24 h" style="width: 12rem">
        <template #body="{ data }">
          <RelativeTime :value="data.lastCallAt" empty="never" />
          <span v-if="data.lastCallFrom" class="small muted mono from">{{ data.lastCallFrom }}</span>
          <div class="small muted">
            {{ data.calls24h }} calls<template v-if="data.refused24h">, {{ data.refused24h }} refused</template>
          </div>
        </template>
      </Column>
      <Column header="" style="width: 6rem">
        <template #body="{ data }"
          ><Tag :value="data.enabled ? 'enabled' : 'disabled'" :severity="data.enabled ? 'success' : 'secondary'"
        /></template>
      </Column>
      <template #mobile="{ item }">
        <div>
          <strong>{{ item.name }}</strong> <code class="muted">…{{ item.keyHint }}</code>
        </div>
        <div class="small muted">{{ item.networks }} networks · {{ item.connectors.length }} connectors</div>
      </template>
    </ListTable>
    <AddCallerDialog v-model:visible="adding" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pnmt-space-2);
}

.from {
  margin-left: var(--pnmt-space-2);
}

.small-tag {
  font-size: 0.75rem;
  padding: 0 var(--pnmt-space-1);
}

.small {
  font-size: 0.857rem;
}
</style>
