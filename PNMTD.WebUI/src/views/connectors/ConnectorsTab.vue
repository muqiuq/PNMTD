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
import AddConnectorDialog from './AddConnectorDialog.vue'
import { modeLabel, typeLabel } from './connectorFormat'

/** *Connectors* (docs/connectors-plan.md §13.3): the list, with a mark where a connector's target would refuse calls now, and *Add connector…*. */
const session = useSession()
const router = useRouter()
const table = useServerTable<S['ConnectorItem']>({
  key: [...keys.connectors, 'list'],
  fetch: (q) => api.connectors.list(q),
  pageSize: 50,
  syncUrl: true,
})
const adding = ref(false)
</script>

<template>
  <div class="tab">
    <div class="toolbar">
      <InputText v-model="table.search.value" placeholder="Search" aria-label="Search connectors" size="small" />
      <span class="muted small">A connector lets one outside system do one thing on one server, and nothing else.</span>
      <Button
        v-if="session.can('Connectors', 'Manage')"
        label="Add connector…"
        icon="pi pi-plus"
        @click="adding = true"
      />
    </div>
    <ListTable
      :table="table"
      what="connectors"
      selectable
      empty-text="No connector yet."
      @select="(c) => router.push({ name: 'connector', params: { id: c.id } })"
    >
      <Column header="Connector">
        <template #body="{ data }">
          <i
            v-if="data.warnings.length"
            v-tooltip="data.warnings.map((w: S['ConnectorWarning']) => w.message).join('\n')"
            class="pi pi-exclamation-triangle warn"
            :aria-label="`${data.warnings.length} warnings`"
          />
          <strong>{{ data.name }}</strong>
          <div class="small muted">{{ typeLabel(data.type) }} · /connector/v1/{{ data.slug }}</div>
        </template>
      </Column>
      <Column header="Target">
        <template #body="{ data }">
          <span class="small">{{ data.server }}</span>
          <div class="small muted">{{ data.function ? `${data.function} · ` : '' }}{{ data.scriptName }}</div>
        </template>
      </Column>
      <Column header="Mode" style="width: 6rem">
        <template #body="{ data }">{{ modeLabel(data.mode) }}</template>
      </Column>
      <Column header="Callers">
        <template #body="{ data }">
          <span v-if="!data.callers.length" class="muted small">none</span>
          <span v-for="c in data.callers" :key="c.id" class="caller small">
            {{ c.name }}<Tag v-if="c.anyAddress" value="any address" severity="warn" class="small-tag" />
          </span>
        </template>
      </Column>
      <Column header="Review" style="width: 6rem">
        <template #body="{ data }">{{ data.review ? 'on' : '' }}</template>
      </Column>
      <Column header="Last call · 24 h" style="width: 10rem">
        <template #body="{ data }">
          <RelativeTime :value="data.lastCallAt" empty="never" />
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
          <strong>{{ item.name }}</strong>
          <Tag :value="item.enabled ? 'enabled' : 'disabled'" :severity="item.enabled ? 'success' : 'secondary'" />
        </div>
        <div class="small muted">{{ item.server }} · {{ item.scriptName }}</div>
      </template>
    </ListTable>
    <AddConnectorDialog v-model:visible="adding" />
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

.warn {
  color: var(--pnmt-warn);
  margin-right: var(--pnmt-space-1);
}

.caller {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  margin-right: var(--pnmt-space-2);
}

.small-tag {
  font-size: 0.75rem;
  padding: 0 var(--pnmt-space-1);
}

.small {
  font-size: 0.857rem;
}
</style>
