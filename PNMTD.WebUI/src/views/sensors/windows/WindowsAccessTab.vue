<script setup lang="ts">
import Column from 'primevue/column'
import Message from 'primevue/message'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref, toRef } from 'vue'
import { api, keys, type S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import SectionBar from './SectionBar.vue'
import WindowsSessions from './WindowsSessions.vue'
import { useWindowsSection } from './useWindowsSection'

/**
 * Logins and connections (docs/windows-sensor-plan.md §4.7, §10.2): the sessions now, every relevant login (admins on
 * every channel, everyone by console, RDP, SSH and WinRM; failures aggregated), and the audit policy's state with the
 * commands for what is missing. The User role: names and addresses.
 */
const props = defineProps<{ server: S['WindowsServerDetail']; canWrite: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const section = useWindowsSection(id, 'access')
const access = computed(() => section.data.value?.access ?? null)

const outcome = ref<S['LogonOutcome'] | null>(null)
const kind = ref<S['LogonKind'] | null>(null)
const adminsOnly = ref(false)
const filters = computed(() => ({
  outcome: outcome.value ?? undefined,
  kind: kind.value ?? undefined,
  admin: adminsOnly.value ? true : undefined,
}))
const table = useServerTable<S['WindowsLoginItem']>({
  key: [...keys.windowsServer(props.server.server.sensorId), 'logins'],
  fetch: (q) => api.windows.logins(props.server.server.sensorId, q),
  pageSize: 50,
  filters,
  refetchInterval: 15_000,
})
const kinds: S['LogonKind'][] = [
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
const kindLabel = (k: S['LogonKind']) =>
  ({ Rdp: 'RDP', RdpConnection: 'RDP connection', Ssh: 'SSH', WinRm: 'WinRM', Explicit: 'explicit credentials' })[
    k as string
  ] ?? k.toLowerCase()
const who = (l: S['WindowsLoginItem']) => (l.domain ? `${l.domain}\\${l.user}` : l.user)
</script>

<template>
  <div class="tab">
    <SectionBar
      :sensor-id="id"
      section="access"
      :view="section.data.value"
      :loading="section.loading.value"
      :error="section.error.value"
      :can-write="canWrite"
      :connected="server.server.connected"
    />
    <div v-if="access" class="two-columns">
      <section class="panel">
        <div class="panel-header">Logged on now</div>
        <div class="panel-body">
          <WindowsSessions :sessions="access.sessions" :last24-hours="access.last24Hours" />
        </div>
      </section>
      <section class="panel">
        <div class="panel-header">
          The audit policy
          <Tag v-if="access.gaps.length" :value="`${access.gaps.length} gaps`" severity="warn" />
          <Tag v-else value="complete" severity="success" />
        </div>
        <div class="panel-body">
          <Message v-if="access.gaps.length" severity="warn" :closable="false" size="small">
            Logins can't be seen completely. The agent changes nothing; run these as an administrator on the server:
          </Message>
          <CopyField v-for="g in access.gaps" :key="g" :value="g" label="Command" />
          <ul class="plain small">
            <li v-for="a in access.auditPolicy" :key="a.guid">
              {{ a.subcategory }}:
              <span :class="{ warn: a.needsSuccess && !a.success }">success {{ a.success ? 'on' : 'off' }}</span
              >,
              <span :class="{ warn: a.needsFailure && !a.failure }">failure {{ a.failure ? 'on' : 'off' }}</span>
            </li>
            <li v-for="l in access.logs" :key="l.name">
              {{ l.name }}:
              <span :class="{ warn: l.exists && !l.enabled }">{{
                !l.exists ? 'not present' : l.enabled ? 'on' : 'off'
              }}</span>
            </li>
            <li v-if="access.securityLog">
              The Security log holds
              <strong>{{ access.securityLog.spanHours ?? '?' }} hours</strong>
              ({{ Math.round(access.securityLog.maxBytes / 1024 / 1024) }} MB at most).
            </li>
          </ul>
        </div>
      </section>
    </div>
    <section class="panel">
      <div class="panel-header">Logins and connections</div>
      <div class="panel-body toolbar">
        <Select
          v-model="outcome"
          :options="['Success', 'Failure']"
          show-clear
          size="small"
          placeholder="Every outcome"
        />
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
            <div v-if="data.count > 1" class="muted small">
              {{ data.count }}× since <RelativeTime :value="data.at" />
            </div>
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
  gap: var(--pnmt-space-2);
  align-items: center;
  flex-wrap: wrap;
}

.plain {
  list-style: none;
  margin: var(--pnmt-space-2) 0 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.check {
  display: flex;
  gap: var(--pnmt-space-1);
  align-items: center;
}

.warn {
  color: var(--pnmt-warn);
  font-weight: 600;
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
