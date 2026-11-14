<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import AddServerDialog from './AddServerDialog.vue'
import { platformIcon, tokenStateSeverity } from './agentFormat'

/**
 * Join tokens (docs/windows-sensor-plan.md §10.1; docs/linux-sensor-plan.md §11.1), each for one platform: pending, used,
 * expired and revoked, never the token itself.
 */
const state = ref<S['AgentJoinTokenState'] | null>(null)
const filters = computed(() => ({ state: state.value ?? undefined }))
const table = useServerTable<S['AgentJoinTokenItem']>({
  key: [...keys.agents, 'tokens'],
  fetch: (q) => api.agents.tokens(q),
  pageSize: 50,
  filters,
})
const { ask, done } = useActions()
async function revoke(t: S['AgentJoinTokenItem']) {
  if (!(await ask('Revoke the token?', `${t.prefix}… for ${t.host.name} can no longer be used.`, 'Revoke'))) return
  await done(() => api.agents.revokeToken(t.id), { success: 'Token revoked', invalidate: [keys.agents] })
}
const adding = ref(false)
const states: S['AgentJoinTokenState'][] = ['Pending', 'Used', 'Expired', 'Revoked']
</script>

<template>
  <div class="tab">
    <div class="toolbar">
      <Select v-model="state" :options="states" show-clear size="small" placeholder="Every state" aria-label="State" />
      <span class="spacer" />
      <Button label="Add server" icon="pi pi-plus" size="small" @click="adding = true" />
    </div>
    <ListTable :table="table" what="tokens" empty-text="No join token yet.">
      <Column header="Token" style="width: 7rem">
        <template #body="{ data }"
          ><span class="mono">{{ data.prefix }}…</span></template
        >
      </Column>
      <Column header="Platform" style="width: 7rem">
        <template #body="{ data }"
          ><span class="line"
            ><i :class="platformIcon(data.platform)" aria-hidden="true" />{{ data.platform }}</span
          ></template
        >
      </Column>
      <Column header="For">
        <template #body="{ data }">
          {{ data.host.name }}<template v-if="data.name"> · {{ data.name }}</template>
          <div v-if="data.rejoin" class="small muted">re-join of {{ data.rejoin.name }}</div>
        </template>
      </Column>
      <Column header="State" style="width: 8rem">
        <template #body="{ data }"><Tag :value="data.state" :severity="tokenStateSeverity(data.state)" /></template>
      </Column>
      <Column header="Created" style="width: 11rem">
        <template #body="{ data }">
          <RelativeTime :value="data.createdAt" />
          <div class="small muted">{{ data.createdByName }}</div>
        </template>
      </Column>
      <Column header="Until" style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.expiresAt" /></template>
      </Column>
      <Column header="Used by">
        <template #body="{ data }">
          <RouterLink v-if="data.result" :to="{ name: 'sensor', params: { id: data.result.id } }">{{
            data.result.name
          }}</RouterLink>
          <div v-if="data.usedFromAddress" class="small muted mono">{{ data.usedFromAddress }}</div>
        </template>
      </Column>
      <Column style="width: 6rem">
        <template #body="{ data }">
          <Button
            v-if="data.state === 'Pending'"
            label="Revoke"
            size="small"
            text
            severity="danger"
            @click="revoke(data)"
          />
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <span class="mono">{{ item.prefix }}…</span>
          <Tag :value="item.state" :severity="tokenStateSeverity(item.state)" />
          {{ item.host.name }} · {{ item.platform }}
        </div>
      </template>
    </ListTable>
    <AddServerDialog v-model:visible="adding" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.toolbar,
.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.spacer {
  flex: 1;
}

.small {
  font-size: 0.857rem;
}
</style>
