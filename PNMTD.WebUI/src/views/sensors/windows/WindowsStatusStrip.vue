<script setup lang="ts">
import Tag from 'primevue/tag'
import { computed } from 'vue'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { connection, skewText, uptime } from '@/views/admin/windows/windowsFormat'

/** A server's state at a glance (docs/windows-sensor-plan.md §10.2): the connection, Windows, uptime, the agent. */
const props = defineProps<{ server: S['WindowsServerDetail'] | undefined }>()
const item = computed(() => props.server?.server)
</script>

<template>
  <div v-if="server && item" class="strip panel">
    <div class="cell">
      <span class="label">Agent</span>
      <span class="value">
        <Tag :value="connection(item).label" :severity="connection(item).severity" />
        <span class="mono small">{{ item.agentVersion }}</span>
        <Tag v-if="item.outdated" value="outdated" severity="warn" />
      </span>
      <span class="muted small">seen <RelativeTime :value="item.lastSeenAt" /></span>
    </div>
    <div class="cell">
      <span class="label">Host name</span>
      <span class="value mono">{{ item.hostname }}</span>
      <span class="muted small">{{ server.fqdn ?? item.domain ?? '' }}</span>
    </div>
    <div class="cell">
      <span class="label">Windows</span>
      <span class="value">{{ item.osCaption ?? '–' }}</span>
      <span class="muted small tag-line"
        ><span>{{ server.osDisplayVersion ?? '' }} {{ item.osBuild }}</span>
        <Tag v-if="!item.osSupported" value="not a supported version" severity="warn"
      /></span>
    </div>
    <div class="cell">
      <span class="label">Uptime</span>
      <span class="value">{{ uptime(item.bootTime) ?? '–' }}</span>
      <span v-if="server.heartbeat?.rebootPending" class="small warn">reboot pending</span>
    </div>
    <div v-if="server.heartbeat" class="cell">
      <span class="label">Load</span>
      <span class="value"
        >CPU {{ server.heartbeat.cpuPercent?.toFixed(0) ?? '–' }} % · memory
        {{ server.heartbeat.memoryPercent?.toFixed(0) ?? '–' }} %</span
      >
      <span v-if="server.heartbeat.lowestVolume" class="muted small"
        >{{ server.heartbeat.lowestVolume }} {{ server.heartbeat.lowestVolumeFreePercent?.toFixed(0) }} % free</span
      >
    </div>
    <div v-if="skewText(item.clockSkewSeconds)" class="cell">
      <span class="label">Clock</span>
      <span class="value warn">{{ skewText(item.clockSkewSeconds) }}</span>
    </div>
  </div>
</template>

<style scoped>
.strip {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-4);
  padding: var(--pnmt-space-3);
}

.cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 8rem;
}

.label {
  font-size: 0.786rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pnmt-muted);
}

.value {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  font-weight: 500;
}

.small {
  font-size: 0.857rem;
}

.warn {
  color: var(--pnmt-warn);
}

.tag-line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
}
</style>
