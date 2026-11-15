<script setup lang="ts">
import { computed } from 'vue'
import type { S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import { linuxUpdateLine, updateHow, windowsUpdateLine } from '@/views/admin/servers/agentFormat'

/**
 * How to update an outdated agent (docs/agent-update-plan.md §8.3): on the server, never from PNMT. Linux: the line, as root;
 * Windows from 1.8: the line in PowerShell as an administrator, or Update in the agent's window; a Windows agent before 1.8:
 * the new executable from the Download tab, run on the server once.
 */
const props = defineProps<{ server: S['AgentServerItem'] }>()
const how = computed(() => updateHow(props.server.platform, props.server.agentVersion))
</script>

<template>
  <div v-if="server.outdated" class="hint">
    <template v-if="how === 'linux'">
      <span class="small muted"
        >Run it on the server, as root: PNMT can't start an update. It checks PNMT's signature.</span
      >
      <CopyField :value="linuxUpdateLine" label="Update line" />
    </template>
    <template v-else-if="how === 'windows'">
      <span class="small muted"
        >Run it on the server in PowerShell as an administrator, or use Update in the agent's window: PNMT can't start
        an update.</span
      >
      <CopyField :value="windowsUpdateLine" label="Update line" />
    </template>
    <span v-else class="small muted">
      This agent can't download from PNMT yet: get the executable from
      <RouterLink :to="{ name: 'servers', params: { tab: 'download' } }">Admin → Servers → Download</RouterLink> and run
      it on the server as an administrator; its window offers Update. From 1.8 on, the agent updates from PNMT.
    </span>
  </div>
</template>

<style scoped>
.hint {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.small {
  font-size: 0.857rem;
}
</style>
