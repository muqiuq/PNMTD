<script setup lang="ts">
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { logKindLabel, logKindSeverity } from '@/views/admin/servers/agentFormat'
import AgentSessions from '../agent/AgentSessions.vue'
import AgentUpdateHint from '../agent/AgentUpdateHint.vue'
import WindowsOverviewExtras from './WindowsOverviewExtras.vue'
import WindowsSystemPanel from './WindowsSystemPanel.vue'

/**
 * A server's overview (docs/windows-sensor-plan.md §10.2): who is logged on now, the update when the agent is older than the
 * version Windows agents get (docs/agent-update-plan.md §8.3), the agent's recent problems, and what the later sections add
 * (remote access, logins, diagnostics, changes).
 */
const props = defineProps<{ server: S['AgentServerDetail']; canWrite: boolean }>()
const sessions = computed(() => props.server.heartbeat?.sessions ?? [])
</script>

<template>
  <div class="two-columns">
    <div class="column">
      <Message v-if="server.server.revoked" severity="error" :closable="false">
        The agent was revoked<template v-if="server.revokedReason">: {{ server.revokedReason }}</template
        >. Its key is deleted; a re-join token brings it back.
      </Message>
      <Message v-else-if="server.server.uninstalled" severity="secondary" :closable="false">
        The service was uninstalled <RelativeTime :value="server.uninstalledAt" />. The server and its history stay.
      </Message>
      <slot name="notes" />
      <section class="panel">
        <div class="panel-header">Logged on now</div>
        <div class="panel-body">
          <p v-if="!server.heartbeat" class="muted">No heartbeat yet.</p>
          <AgentSessions v-else :sessions="sessions" />
        </div>
      </section>
      <WindowsSystemPanel :server="server" :can-write="canWrite" />
      <slot name="left" />
    </div>
    <div class="column">
      <section v-if="server.server.outdated" class="panel">
        <div class="panel-header">
          Update available: {{ server.server.chosenVersion }}
          <span class="spacer" />
          <span class="small muted mono">this agent is {{ server.server.agentVersion }}</span>
        </div>
        <div class="panel-body"><AgentUpdateHint :server="server.server" /></div>
      </section>
      <section class="panel">
        <div class="panel-header">The agent's recent problems</div>
        <div class="panel-body">
          <p v-if="!server.recentProblems.length" class="muted">None reported.</p>
          <ul v-else class="plain">
            <li v-for="p in server.recentProblems" :key="p.id">
              <Tag :value="logKindLabel(p.kind)" :severity="logKindSeverity(p.kind)" />
              <RelativeTime :value="p.at" /> · {{ p.message }}
            </li>
          </ul>
        </div>
      </section>
      <WindowsOverviewExtras :server="server" :can-write="canWrite" />
      <slot name="right" />
    </div>
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

.small {
  font-size: 0.857rem;
}

.plain {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}
</style>
