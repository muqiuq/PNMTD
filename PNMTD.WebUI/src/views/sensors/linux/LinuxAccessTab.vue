<script setup lang="ts">
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed, toRef } from 'vue'
import type { S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { bytes } from '@/views/admin/servers/agentFormat'
import AgentLoginsList from '../agent/AgentLoginsList.vue'
import AgentSessions from '../agent/AgentSessions.vue'
import SectionBar from '../agent/SectionBar.vue'
import { useLinuxSection } from '../agent/useAgentSection'

/**
 * Logins and elevations on a Linux server (docs/linux-sensor-plan.md §5.6): the sessions now and the last 24 hours (scanners
 * counted apart), the logging check (a persistent journal, its span, sshd's LogLevel, sudo's logging) with the command that
 * fixes what is missing (the agent changes nothing), and every login, sudo, su and file transfer in the shared list, with the
 * keys. The User role: names and addresses.
 */
const props = defineProps<{ server: S['AgentServerDetail']; canWrite: boolean; keyFingerprint?: string | null }>()
const id = toRef(() => props.server.server.sensorId)
const section = useLinuxSection(id, 'access')
const access = computed(() => section.data.value?.access ?? null)
const logging = computed(() => access.value?.logging ?? null)
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
        <div class="panel-header">Logged in now</div>
        <div class="panel-body">
          <AgentSessions
            :sessions="access.sessions"
            :last24-hours="access.last24Hours"
            :elevations="access.elevations24Hours"
            :scanners="access.invalidUserFailures24Hours"
          />
        </div>
      </section>
      <section v-if="logging" class="panel">
        <div class="panel-header">
          The logging check
          <Tag v-if="logging.fixes.length" :value="`${logging.fixes.length} to fix`" severity="warn" />
          <Tag v-else value="complete" severity="success" />
        </div>
        <div class="panel-body content">
          <dl class="facts">
            <dt>Journal</dt>
            <dd>
              <span :class="{ warn: !logging.journalPersistent }">{{
                logging.journalPersistent ? 'persistent' : 'not persistent: lost at every reboot'
              }}</span>
              <div class="muted small">
                <template v-if="logging.spanDays !== null"
                  >holds {{ Math.floor(logging.spanDays) }} days<template v-if="logging.oldestEntryAt">
                    (since <RelativeTime :value="logging.oldestEntryAt" />)</template
                  ></template
                ><template v-if="logging.journalBytes"> · {{ bytes(logging.journalBytes) }}</template>
              </div>
            </dd>
            <dt>sshd's LogLevel</dt>
            <dd>
              <span class="mono">{{ logging.sshdLogLevel ?? '–' }}</span>
              <span class="muted small">
                ·
                {{
                  logging.sshdLogLevel === 'VERBOSE'
                    ? "failed keys' fingerprints are logged too"
                    : 'accepted keys are logged, failed ones not'
                }}</span
              >
            </dd>
            <dt>sudo's logging</dt>
            <dd :class="{ warn: !logging.sudoLogging }">{{ logging.sudoLogging ? 'on' : 'turned off' }}</dd>
          </dl>
          <template v-if="logging.fixes.length">
            <Message severity="warn" :closable="false" size="small">
              Logins can't be seen completely. The agent changes nothing; run these as root on the server:
            </Message>
            <div v-for="f in logging.fixes" :key="f.key" class="fix">
              <div class="small">{{ f.problem }}</div>
              <CopyField v-if="f.fix" :value="f.fix" :label="`Fix: ${f.problem}`" />
            </div>
          </template>
        </div>
      </section>
    </div>
    <AgentLoginsList :server="server" :key-fingerprint="keyFingerprint" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.content {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.fix {
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
