<script setup lang="ts">
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed, toRef } from 'vue'
import type { S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import AgentLoginsList from '../agent/AgentLoginsList.vue'
import AgentSessions from '../agent/AgentSessions.vue'
import SectionBar from '../agent/SectionBar.vue'
import { useAgentSection } from '../agent/useAgentSection'

/**
 * Logins and connections (docs/windows-sensor-plan.md §4.7, §10.2): the sessions now, the audit policy's state with the
 * commands for what is missing (Windows' own), and every relevant login (admins on every channel, everyone by console,
 * RDP, SSH and WinRM; failures aggregated) in the shared list. The User role: names and addresses.
 */
const props = defineProps<{ server: S['AgentServerDetail']; canWrite: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const section = useAgentSection(id, 'access')
const access = computed(() => section.data.value?.access ?? null)
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
          <AgentSessions :sessions="access.sessions" :last24-hours="access.last24Hours" />
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
    <AgentLoginsList :server="server" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.plain {
  list-style: none;
  margin: var(--pnmt-space-2) 0 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.warn {
  color: var(--pnmt-warn);
  font-weight: 600;
}

.small {
  font-size: 0.857rem;
}
</style>
