<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import { computed, ref } from 'vue'
import type { S } from '@/api/resources'
import RunScriptDialog from '@/views/admin/windows/RunScriptDialog.vue'
import ScriptRunsList from '@/views/admin/windows/ScriptRunsList.vue'

/** A server's scripts (docs/windows-sensor-plan.md §7.3): Run… on this server, and its runs. Admin. */
const props = defineProps<{ server: S['WindowsServerDetail'] }>()
const allowed = computed(() => props.server.capabilities.includes('scripts.v1'))
const running = ref(false)
</script>

<template>
  <div class="tab">
    <Message v-if="!allowed" severity="info" :closable="false" size="small">
      Scripts are not allowed on this server: an administrator turns them on in the agent's window there (Scripts from
      PNMT).
    </Message>
    <div class="toolbar">
      <span class="spacer" />
      <Button label="Run a script…" icon="pi pi-play" size="small" :disabled="!allowed" @click="running = true" />
    </div>
    <ScriptRunsList :sensor-id="server.server.sensorId" />
    <RunScriptDialog v-model:visible="running" :script-id="null" :sensor-id="server.server.sensorId" />
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
}

.spacer {
  flex: 1;
}
</style>
