<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import { shortFingerprint } from '@/views/sensors/linux/linuxFormat'
import { reasonLabel, removalReasons, removalStateLabel, removalStateSeverity, removalStates } from './sshKeyFormat'
import { useQueryParam } from './useQueryParam'

/**
 * Every removal (docs/linux-sensor-plan.md §6.5, §11.4): when, by whom, why (a server, a key, a retirement, an offboarding, an
 * unregistered key), the server, account and file, the key, the state (pending, sent, removed, not found, refused with the reason,
 * failed, expired) and the backup's name. One dialog's removals by ?batchId=.
 */
const emit = defineEmits<{ open: [id: string] }>()
const route = useRoute()
const router = useRouter()
const state = useQueryParam<S['SshKeyRemovalState']>('state')
const reason = ref<S['SshKeyRemovalReason'] | null>(null)
const batchId = computed(() => (typeof route.query.batchId === 'string' ? route.query.batchId : undefined))
const filters = computed(() => ({
  state: state.value ?? undefined,
  reason: reason.value ?? undefined,
  batchId: batchId.value,
}))
const table = useServerTable<S['SshKeyRemovalItem']>({
  key: [...keys.sshKeys, 'removals'],
  fetch: (q) => api.sshKeys.removals(q),
  pageSize: 50,
  filters,
  syncUrl: true,
  // Pending and sent ones change as servers answer.
  refetchInterval: 10_000,
})
const stateOptions = removalStates.map((s) => ({ label: removalStateLabel(s), value: s }))
const reasonOptions = removalReasons.map((r) => ({ label: reasonLabel(r), value: r }))
const everyBatch = () => router.replace({ query: { ...route.query, batchId: undefined } })
</script>

<template>
  <div class="tab">
    <FilterBar v-model:search="table.search.value" placeholder="Key, server, account, file or fingerprint">
      <Select
        v-model="state"
        :options="stateOptions"
        option-label="label"
        option-value="value"
        show-clear
        size="small"
        placeholder="Every state"
        aria-label="State"
      />
      <Select
        v-model="reason"
        :options="reasonOptions"
        option-label="label"
        option-value="value"
        show-clear
        size="small"
        placeholder="Every reason"
        aria-label="Reason"
      />
      <Button
        v-if="batchId"
        label="One dialog's removals"
        icon="pi pi-times"
        icon-pos="right"
        size="small"
        severity="secondary"
        outlined
        @click="everyBatch"
      />
    </FilterBar>
    <ListTable :table="table" what="removals" empty-text="No key removed yet.">
      <Column header="When" style="width: 10rem">
        <template #body="{ data }">
          <RelativeTime :value="data.requestedAt" />
          <div class="muted small">{{ data.requestedByName ?? '' }}</div>
        </template>
      </Column>
      <Column header="Key">
        <template #body="{ data }">
          <a v-if="data.keyId" href="#" @click.prevent="emit('open', data.keyId)">{{ data.keyName ?? 'a key' }}</a>
          <span v-else>{{ data.keyName ?? 'unregistered' }}</span>
          <div class="mono small muted" :title="data.fingerprint">{{ shortFingerprint(data.fingerprint) }}</div>
        </template>
      </Column>
      <Column header="Where">
        <template #body="{ data }">
          <RouterLink v-if="data.server" :to="{ name: 'sensor', params: { id: data.server.id, tab: 'ssh-keys' } }">{{
            data.serverName
          }}</RouterLink>
          <span v-else>{{ data.serverName }}</span>
          · <strong>{{ data.account }}</strong>
          <div class="mono small muted">{{ data.path }}</div>
        </template>
      </Column>
      <Column header="Why" style="width: 11rem">
        <template #body="{ data }"
          ><span class="small">{{ reasonLabel(data.reason) }}</span></template
        >
      </Column>
      <Column header="State" style="width: 22rem">
        <template #body="{ data }">
          <Tag :value="removalStateLabel(data.state)" :severity="removalStateSeverity(data.state)" />
          <span v-if="data.appliedAt" class="muted small"> <RelativeTime :value="data.appliedAt" /></span>
          <span v-else-if="data.sentAt" class="muted small"> sent <RelativeTime :value="data.sentAt" /></span>
          <div v-if="data.message" class="small">{{ data.message }}</div>
          <div v-if="data.backup" class="small muted">
            {{ data.linesRemoved ?? '' }} {{ data.linesRemoved === 1 ? 'line' : 'lines' }} · backup
            <span class="mono">{{ data.backup }}</span>
          </div>
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <strong>{{ item.keyName ?? shortFingerprint(item.fingerprint) }}</strong>
          <Tag :value="removalStateLabel(item.state)" :severity="removalStateSeverity(item.state)" />
        </div>
        <div class="small muted">
          <RelativeTime :value="item.requestedAt" /> · {{ item.serverName }} · {{ item.account }}
        </div>
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

.line {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
  align-items: center;
}

.small {
  font-size: 0.857rem;
}
</style>
