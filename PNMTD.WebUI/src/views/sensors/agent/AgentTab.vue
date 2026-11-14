<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import AddServerDialog from '@/views/admin/servers/AddServerDialog.vue'
import AgentLogTab from '@/views/admin/servers/AgentLogTab.vue'
import { platformCodes, skewText } from '@/views/admin/servers/agentFormat'

/**
 * A server's agent (docs/windows-sensor-plan.md §10.2; docs/linux-sensor-plan.md §11.3), on either platform: the connection,
 * the version, both keys' fingerprints, the switches for its events with the platform's codes, Collect now, Send log,
 * Re-join and Revoke, and its log.
 */
const props = defineProps<{ server: S['AgentServerDetail']; canWrite: boolean; isAdmin: boolean }>()
const id = () => props.server.server.sensorId
const linux = computed(() => props.server.server.platform === 'Linux')
const codes = computed(() => platformCodes(props.server.server.platform))

const settings = ref<S['AgentServerSettings']>({ ...props.server.settings })
watch(
  () => props.server.settings,
  (s) => (settings.value = { ...s }),
)
const dirty = () => JSON.stringify(settings.value) !== JSON.stringify(props.server.settings)

const { run, done, ask, busy } = useActions()
const invalidate = [keys.agents, keys.sensors]
const save = () => run(() => api.agents.settings(id(), settings.value), { success: 'Saved', invalidate })
const collect = () =>
  run(
    async () => {
      const r = await api.agents.collect(id())
      if (!r.ok) throw new Error(r.error ?? 'The agent refused.')
      return r
    },
    { success: 'The agent collects now', invalidate },
  )
const lines = ref<string[] | null>(null)
async function sendLog() {
  const file = await run(() => api.agents.sendLog(id()))
  if (file) lines.value = file.lines
}
async function revoke() {
  if (
    !(await ask(
      'Revoke the agent?',
      'Its key is deleted and its connection closed. The server and its history stay; a re-join token brings it back.',
      'Revoke',
    ))
  )
    return
  await done(() => api.agents.revoke(id(), null), { success: 'Revoked', invalidate })
}
const rejoining = ref(false)
</script>

<template>
  <div class="two-columns">
    <div class="column">
      <section class="panel">
        <div class="panel-header">
          The agent
          <span class="spacer" />
          <Button
            v-if="canWrite"
            label="Collect now"
            icon="pi pi-refresh"
            size="small"
            severity="secondary"
            :disabled="!server.server.connected"
            :loading="busy"
            @click="collect"
          />
        </div>
        <div class="panel-body">
          <dl class="facts">
            <dt>Connection</dt>
            <dd>
              <template v-if="server.server.connected"
                >since <RelativeTime :value="server.connectedAt" /> from
                <span class="mono">{{ server.remoteAddress }}</span></template
              >
              <template v-else
                >offline since <RelativeTime :value="server.disconnectedAt ?? server.server.lastSeenAt"
              /></template>
            </dd>
            <dt>Version</dt>
            <dd>
              <span class="mono">{{ server.server.agentVersion }}</span> ({{ server.server.architecture }})
              <Tag v-if="server.server.outdated" :value="`PNMT is ${server.pnmtVersion}`" severity="warn" />
            </dd>
            <dt>Joined</dt>
            <dd><RelativeTime :value="server.joinedAt" /></dd>
            <dt>Its key</dt>
            <dd class="mono small">{{ server.keyFingerprint }}</dd>
            <dt>PNMTD's key</dt>
            <dd class="mono small">{{ server.pnmtdFingerprint }}</dd>
            <dt>Configuration</dt>
            <dd>
              version {{ server.configVersion }}
              <span v-if="server.configVersion !== server.currentConfigVersion" class="muted small"
                >(PNMT's is {{ server.currentConfigVersion }})</span
              >
            </dd>
            <dt>Clock</dt>
            <dd>{{ skewText(server.server.clockSkewSeconds) ?? 'right' }}</dd>
            <dt>Capabilities</dt>
            <dd class="small">{{ server.capabilities.join(', ') || '–' }}</dd>
            <template v-if="server.machineId">
              <dt>{{ linux ? 'Machine id' : 'MachineGuid' }}</dt>
              <dd class="mono small">{{ server.machineId }}</dd>
            </template>
            <template v-if="server.sameMachine.length">
              <dt>Possibly the same machine</dt>
              <dd>
                <RouterLink
                  v-for="m in server.sameMachine"
                  :key="m.id"
                  :to="{ name: 'sensor', params: { id: m.id, tab: 'agent' } }"
                  >{{ m.name }}
                </RouterLink>
              </dd>
            </template>
          </dl>
          <div v-if="isAdmin" class="actions">
            <Button
              label="Send log"
              icon="pi pi-file"
              size="small"
              severity="secondary"
              :disabled="!server.server.connected"
              @click="sendLog"
            />
            <Button label="Re-join…" icon="pi pi-key" size="small" severity="secondary" @click="rejoining = true" />
            <Button
              v-if="!server.server.revoked"
              label="Revoke…"
              icon="pi pi-ban"
              size="small"
              severity="danger"
              outlined
              @click="revoke"
            />
          </div>
        </div>
      </section>
      <section class="panel">
        <div class="panel-header">Events</div>
        <form class="panel-body form" @submit.prevent="save">
          <label class="check"
            ><Checkbox v-model="settings.adminLoginEvents" binary :disabled="!canWrite" /> {{ codes.adminLogin }} for
            admin logins ({{ linux ? 'SSH, console' : 'console, RDP, SSH, WinRM' }})</label
          >
          <label class="check"
            ><Checkbox v-model="settings.failedLoginEvents" binary :disabled="!canWrite" />
            {{ codes.loginFailures }} for bursts of failed logins, from
            <InputNumber
              v-model="settings.failedLoginThreshold"
              :min="2"
              :max="1000"
              size="small"
              :disabled="!canWrite"
              input-class="threshold"
            />
            in 10 minutes</label
          >
          <label class="check"
            ><Checkbox v-model="settings.criticalEvents" binary :disabled="!canWrite" /> {{ codes.critical }} for new
            {{
              linux ? 'critical entries in the journal' : 'Critical entries in the System and Application logs'
            }}</label
          >
          <small class="field-hint">Nothing is sent without a notification rule.</small>
          <div v-if="canWrite" class="form-actions">
            <Button type="submit" label="Save" size="small" :disabled="!dirty()" :loading="busy" />
          </div>
        </form>
      </section>
    </div>
    <div class="column">
      <section v-if="isAdmin" class="panel">
        <div class="panel-header">Agent log</div>
        <div class="panel-body"><AgentLogTab :sensor-id="server.server.sensorId" /></div>
      </section>
    </div>
    <Dialog
      :visible="lines !== null"
      header="The agent's log file"
      modal
      :style="{ width: '60rem' }"
      :breakpoints="{ '960px': '95vw' }"
      @update:visible="(v: boolean) => !v && (lines = null)"
    >
      <pre class="log mono">{{ (lines ?? []).join('\n') }}</pre>
    </Dialog>
    <AddServerDialog
      v-model:visible="rejoining"
      :rejoin="{ sensorId: server.server.sensorId, name: `${server.server.host.name} / ${server.server.name}` }"
    />
  </div>
</template>

<style scoped>
.column {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.spacer {
  flex: 1;
}

.actions {
  display: flex;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
  margin-top: var(--pnmt-space-3);
}

.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.check :deep(.threshold) {
  width: 4rem;
}

.log {
  white-space: pre-wrap;
  font-size: 0.857rem;
  max-height: 70vh;
  overflow: auto;
}

.small {
  font-size: 0.857rem;
}
</style>
