<script setup lang="ts">
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'

/**
 * The logins and connections of a server (docs/windows-sensor-plan.md §4.7; docs/linux-sensor-plan.md §5.6), with their
 * filters: every admin login, failures aggregated, the kinds of its platform. On the Access tab of either platform. The User
 * role: names and addresses.
 */
const props = defineProps<{ server: S['AgentServerDetail'] }>()
const outcome = ref<S['LogonOutcome'] | null>(null)
const kind = ref<S['LogonKind'] | null>(null)
const adminsOnly = ref(false)
const filters = computed(() => ({
  outcome: outcome.value ?? undefined,
  kind: kind.value ?? undefined,
  admin: adminsOnly.value ? true : undefined,
}))
const table = useServerTable<S['AgentLoginItem']>({
  key: [...keys.agentServer(props.server.server.sensorId), 'logins'],
  fetch: (q) => api.agents.logins(props.server.server.sensorId, q),
  pageSize: 50,
  filters,
  refetchInterval: 15_000,
})
const windowsKinds: S['LogonKind'][] = [
  'Console',
  'Rdp',
  'RdpConnection',
  'Ssh',
  'WinRm',
  'Network',
  'Cached',
  'Unlock',
  'Explicit',
  'Batch',
  'Lockout',
]
const linuxKinds: S['LogonKind'][] = ['Ssh', 'Sftp', 'Console', 'Sudo', 'Su', 'Lockout']
const kinds = computed(() => (props.server.server.platform === 'Linux' ? linuxKinds : windowsKinds))
const kindLabel = (k: S['LogonKind']) =>
  (
    ({
      Rdp: 'RDP',
      RdpConnection: 'RDP connection',
      Ssh: 'SSH',
      Sftp: 'SFTP',
      WinRm: 'WinRM',
      Explicit: 'explicit credentials',
    }) as Record<string, string>
  )[k] ?? k.toLowerCase()
const who = (l: S['AgentLoginItem']) => (l.domain ? `${l.domain}\\${l.user}` : l.user)
</script>

<template>
  <section class="panel">
    <div class="panel-header">Logins and connections</div>
    <div class="panel-body toolbar">
      <Select v-model="outcome" :options="['Success', 'Failure']" show-clear size="small" placeholder="Every outcome" />
      <Select
        v-model="kind"
        :options="kinds"
        :option-label="(k: S['LogonKind']) => kindLabel(k)"
        show-clear
        size="small"
        placeholder="Every kind"
      />
      <label class="check"><input v-model="adminsOnly" type="checkbox" /> admins only</label>
    </div>
    <ListTable :table="table" what="logins" empty-text="No login recorded yet.">
      <Column header="When" style="width: 9rem">
        <template #body="{ data }">
          <RelativeTime :value="data.lastAt ?? data.at" />
          <div v-if="data.count > 1" class="muted small">{{ data.count }}× since <RelativeTime :value="data.at" /></div>
        </template>
      </Column>
      <Column header="Who">
        <template #body="{ data }">
          <div class="tag-line">
            <strong>{{ who(data) }}</strong>
            <Tag v-if="data.isAdmin" value="admin" severity="warn" class="small" />
            <Tag
              v-if="data.outcome === 'Failure'"
              :value="data.failureReason ?? 'failed'"
              severity="danger"
              class="small"
            />
          </div>
        </template>
      </Column>
      <Column header="How" style="width: 12rem">
        <template #body="{ data }">
          {{ kindLabel(data.kind) }}
          <span class="muted small">{{ data.authPackage }}</span>
          <span v-if="data.elevated" class="muted small"> · elevated</span>
        </template>
      </Column>
      <Column header="From" style="width: 12rem">
        <template #body="{ data }">
          <span class="mono small">{{ data.sourceAddress ?? '' }}</span>
          <div class="muted small">{{ data.workstation }}</div>
        </template>
      </Column>
      <Column header="Logged off" style="width: 8rem">
        <template #body="{ data }"><RelativeTime v-if="data.loggedOffAt" :value="data.loggedOffAt" /></template>
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <strong>{{ who(item) }}</strong> · {{ kindLabel(item.kind) }}
          <Tag v-if="item.outcome === 'Failure'" value="failed" severity="danger" />
        </div>
        <div class="small muted"><RelativeTime :value="item.lastAt ?? item.at" /> · {{ item.sourceAddress }}</div>
      </template>
    </ListTable>
  </section>
</template>

<style scoped>
.toolbar,
.line {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: center;
  flex-wrap: wrap;
}

.check {
  display: flex;
  gap: var(--pnmt-space-1);
  align-items: center;
}

.small {
  font-size: 0.857rem;
}

.tag-line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
}
</style>
