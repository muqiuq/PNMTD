<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed, toRef } from 'vue'
import { api, keys, type S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { connection, logKindLabel, logKindSeverity, logonKindLabel } from '@/views/admin/servers/agentFormat'
import AgentOverviewPanels from '../agent/AgentOverviewPanels.vue'
import AgentSessions from '../agent/AgentSessions.vue'
import { useLinuxSection } from '../agent/useAgentSection'
import LinuxSystemPanel from './LinuxSystemPanel.vue'
import { keyGlance, keyLabel, rightsSource, verdictSeverity } from './linuxFormat'

/**
 * A Linux server's overview (docs/linux-sensor-plan.md §11.2): who is logged in now and the system (host, distribution,
 * kernel, uptime, updates, time, security, administrators); on the right the agent's state and problems, the remote access
 * summary, the last admin logins and elevations with their keys, the failed logins of 24 hours (scanners counted apart), the SSH
 * keys at a glance, the diagnostics and the last 5 changes. Logins and keys need the User role.
 */
const props = defineProps<{ server: S['AgentServerDetail']; canWrite: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const canWrite = toRef(() => props.canWrite)
const remote = useLinuxSection(id, 'remote-access')
const access = useLinuxSection(id, 'access', canWrite)
const sshKeys = useLinuxSection(id, 'ssh-keys', canWrite)
const admins = useQuery({
  queryKey: computed(() => [...keys.agentServer(id.value), 'logins', 'overview-admins']),
  queryFn: () => api.agents.logins(id.value, { admin: true, outcome: 'Success', pageSize: 6 }),
  enabled: canWrite,
  refetchInterval: 30_000,
})
const sessions = computed(() => props.server.heartbeat?.sessions ?? [])
const activeFeatures = computed(() => (remote.data.value?.remoteAccess?.features ?? []).filter((f) => f.active))
const day = computed(() => access.data.value?.access ?? null)
const glance = computed(() => (sshKeys.data.value?.sshKeys ? keyGlance(sshKeys.data.value.sshKeys) : null))
const rights = computed(() => props.server.rights ?? null)
const tab = (t: string) => ({ name: 'sensor', params: { id: id.value, tab: t } })
</script>

<template>
  <div class="two-columns">
    <div class="column">
      <Message v-if="server.server.revoked" severity="error" :closable="false">
        The agent was revoked<template v-if="server.revokedReason">: {{ server.revokedReason }}</template
        >. Its key is deleted; a re-join token brings it back.
      </Message>
      <Message v-else-if="server.server.uninstalled" severity="secondary" :closable="false">
        The agent was uninstalled <RelativeTime :value="server.uninstalledAt" />. The server and its history stay.
      </Message>
      <slot name="notes" />
      <section class="panel">
        <div class="panel-header">Logged in now</div>
        <div class="panel-body">
          <p v-if="!server.heartbeat" class="muted">No heartbeat yet.</p>
          <AgentSessions v-else :sessions="sessions" />
        </div>
      </section>
      <LinuxSystemPanel :server="server" :can-write="canWrite" />
    </div>
    <div class="column">
      <section class="panel">
        <div class="panel-header">
          The agent
          <span class="spacer" />
          <RouterLink :to="tab('agent')" class="small">Agent tab</RouterLink>
        </div>
        <div class="panel-body content">
          <div class="tag-line">
            <Tag :value="connection(server.server).label" :severity="connection(server.server).severity" />
            <span class="mono small">{{ server.server.agentVersion }}</span>
            <Tag
              v-if="server.server.outdated"
              :value="`Update available: ${server.server.chosenVersion}`"
              severity="warn"
            />
            <Tag
              v-else-if="server.server.ahead"
              :value="`newer than ${server.server.chosenVersion}`"
              severity="secondary"
            />
            <Tag
              v-if="rights"
              :value="`rights: ${rightsSource(rights).label}`"
              :severity="rightsSource(rights).severity"
            />
          </div>
          <template v-if="server.server.outdated">
            <span class="small muted">Run it on the server, as root: PNMT can't start an update.</span>
            <CopyField value="pnmt-agent update" label="Update line" />
          </template>
          <Message v-if="rights?.problem" severity="error" :closable="false" size="small">{{ rights.problem }}</Message>
          <p v-if="!server.recentProblems.length" class="muted small">No problems reported.</p>
          <ul v-else class="plain">
            <li v-for="p in server.recentProblems" :key="p.id">
              <Tag :value="logKindLabel(p.kind)" :severity="logKindSeverity(p.kind)" />
              <RelativeTime :value="p.at" /> · {{ p.message }}
            </li>
          </ul>
        </div>
      </section>
      <section class="panel">
        <div class="panel-header">Remote access</div>
        <div class="panel-body">
          <p>
            {{ remote.data.value?.remoteAccess?.summary ?? (remote.loading.value ? 'Loading…' : 'Not reported yet.') }}
          </p>
          <div class="tags">
            <Tag
              v-for="f in activeFeatures"
              :key="f.key"
              :value="`${f.name}${f.ports.length ? ` · firewall ${f.firewall}` : ''}`"
              :severity="f.highlight ? 'danger' : f.ports.length ? verdictSeverity(f.firewall) : 'secondary'"
            />
          </div>
        </div>
      </section>
      <template v-if="canWrite">
        <section class="panel">
          <div class="panel-header">
            Admin logins and elevations
            <span class="spacer" />
            <RouterLink :to="tab('access')" class="small">Access tab</RouterLink>
          </div>
          <ul class="panel-body plain">
            <li v-for="l in admins.data.value?.items ?? []" :key="l.id">
              <RelativeTime :value="l.lastAt ?? l.at" /> · <strong>{{ l.user }}</strong> · {{ logonKindLabel(l.kind)
              }}<template v-if="l.targetUser"> → {{ l.targetUser }}</template>
              <span v-if="l.sourceAddress" class="mono muted"> from {{ l.sourceAddress }}</span>
              <span v-else-if="l.workstation" class="mono muted"> on {{ l.workstation }}</span>
              <span v-if="l.keyFingerprint" class="muted small" :class="{ mono: !l.keyName }" :title="l.keyFingerprint">
                · key {{ keyLabel(l) }}</span
              >
            </li>
            <li v-if="admins.isPending.value" class="muted">Loading…</li>
            <li v-else-if="!admins.data.value?.items.length" class="muted">None recorded.</li>
          </ul>
          <div class="panel-body small day">
            <template v-if="day?.last24Hours">
              In the last 24 hours: <strong>{{ day.last24Hours.failures }}</strong> failed logins for existing
              accounts<template v-if="day.last24Hours.lockouts">, {{ day.last24Hours.lockouts }} lockouts</template>;
              <strong>{{ day.invalidUserFailures24Hours }}</strong> from scanners (accounts that don't exist);
              {{ day.elevations24Hours }} sudo and su.
            </template>
            <span v-else class="muted">{{
              access.loading.value ? 'Loading…' : 'The last 24 hours are not reported yet.'
            }}</span>
          </div>
        </section>
        <section class="panel">
          <div class="panel-header">
            SSH keys
            <span class="spacer" />
            <RouterLink :to="tab('ssh-keys')" class="small">SSH keys tab</RouterLink>
          </div>
          <div class="panel-body">
            <template v-if="glance">
              <p>
                <strong>{{ glance.keys }}</strong> keys in {{ glance.files }} files<template
                  v-if="glance.distinct !== glance.keys"
                >
                  ({{ glance.distinct }} different)</template
                >.
              </p>
              <div class="tags">
                <Tag
                  v-if="server.server.unregisteredKeys"
                  :value="`${server.server.unregisteredKeys} unregistered`"
                  severity="warn"
                />
                <Tag
                  v-if="server.server.retiredKeysAuthorized"
                  :value="`${server.server.retiredKeysAuthorized} retired, still here`"
                  severity="danger"
                />
                <Tag v-if="glance.weak" :value="`${glance.weak} weak`" severity="danger" />
                <Tag
                  v-if="glance.rootWithoutFrom"
                  :value="`${glance.rootWithoutFrom} of root's without from=`"
                  severity="warn"
                />
                <Tag v-if="glance.problems" :value="`${glance.problems} file problems`" severity="warn" />
                <Tag
                  v-if="glance.invalidLines"
                  :value="`${glance.invalidLines} lines that aren't keys`"
                  severity="secondary"
                />
                <Tag
                  v-if="sshKeys.data.value?.sshKeys?.caKeys.length"
                  :value="`${sshKeys.data.value.sshKeys.caKeys.length} CA keys`"
                  severity="warn"
                />
              </div>
            </template>
            <p v-else class="muted">{{ sshKeys.loading.value ? 'Loading…' : 'Not reported yet.' }}</p>
          </div>
        </section>
      </template>
      <AgentOverviewPanels :server="server" />
    </div>
  </div>
</template>

<style scoped>
.column {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.content {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.content p {
  margin: 0;
}

.spacer {
  flex: 1;
}

.plain {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.panel-body.plain {
  padding: var(--pnmt-space-3);
}

.day {
  border-top: 1px solid var(--pnmt-border);
}

.tags,
.tag-line {
  display: flex;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
  flex-wrap: wrap;
  align-items: center;
}

.small {
  font-size: 0.857rem;
}
</style>
