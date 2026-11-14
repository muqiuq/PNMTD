<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import { computed, ref } from 'vue'
import type { S } from '@/api/resources'
import RunScriptDialog from '@/views/admin/servers/RunScriptDialog.vue'
import ScriptRunsList from '@/views/admin/servers/ScriptRunsList.vue'
import WindowsLocalScripts from '../windows/WindowsLocalScripts.vue'

/**
 * A server's scripts (docs/windows-sensor-plan.md §7.2, §7.3; docs/linux-sensor-plan.md §8): on Windows the scripts an
 * administrator registered in the agent's window there, each runnable from here; Run… for the library; and the server's
 * runs. Admin.
 */
const props = defineProps<{ server: S['AgentServerDetail'] }>()
const allowed = computed(() => props.server.capabilities.includes('scripts.v1'))
const windows = computed(() => props.server.server.platform !== 'Linux')
const running = ref(false)
const localName = ref<string | null>(null)
const runDialog = (name: string | null) => {
  localName.value = name
  running.value = true
}
</script>

<template>
  <div class="tab">
    <Message v-if="!allowed" severity="info" :closable="false" size="small">
      <template v-if="windows">
        Scripts are not allowed on this server: an administrator turns them on in the agent's window there (Scripts from
        PNMT).
      </template>
      <template v-else>
        Scripts are not allowed on this server: root allows them in /etc/pnmt-agent/config.yaml there.
      </template>
    </Message>
    <WindowsLocalScripts v-if="windows" :server="server" :allowed="allowed" @run="runDialog" />
    <div class="toolbar">
      <span class="muted small">From PNMT's library:</span>
      <span class="spacer" />
      <Button label="Run a script…" icon="pi pi-play" size="small" :disabled="!allowed" @click="runDialog(null)" />
    </div>
    <ScriptRunsList :sensor-id="server.server.sensorId" />
    <RunScriptDialog
      v-model:visible="running"
      :script-id="null"
      :sensor-id="server.server.sensorId"
      :local-name="localName"
    />
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
}

.spacer {
  flex: 1;
}

.small {
  font-size: 0.857rem;
}
</style>
