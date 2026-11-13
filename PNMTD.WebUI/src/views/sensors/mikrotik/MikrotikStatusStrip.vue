<script setup lang="ts">
import Message from 'primevue/message'
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { formatDuration, formatPercent } from '@/format'
import { formatBytes } from '@/format/bytes'
import { modeLabel } from './mikrotikDraft'

/**
 * A router at a glance (docs/mikrotik-plan.md §11.1): RouterOS and its channel with an available
 * update, the model and serial number, uptime, CPU, memory, storage, firmware, the last poll and
 * the last error, and the server's warnings.
 */
const props = defineProps<{ sensor: S['SensorDetail'] }>()
const m = computed(() => props.sensor.mikrotik!)
const r = computed(() => m.value.resources)
const used = (free: number | null | undefined, total: number | null | undefined) =>
  free != null && total ? (total - free) / total : null
const uptime = computed(() => (r.value?.uptimeSeconds != null ? formatDuration(r.value.uptimeSeconds * 1000) : null))
</script>

<template>
  <section class="panel strip" aria-label="Router status">
    <dl class="stats">
      <div>
        <dt>RouterOS</dt>
        <dd>
          <span class="mono">{{ m.version ?? '–' }}</span>
          <span v-if="m.channel" class="muted small"> · {{ m.channel }}</span>
          <div v-if="m.updateAvailable" class="small update">
            <RouterLink :to="{ name: 'sensor', params: { id: sensor.id, tab: 'setup' } }"
              >{{ m.latestVersion }} available</RouterLink
            >
          </div>
        </dd>
      </div>
      <div>
        <dt>Router</dt>
        <dd>
          {{ m.identity ?? '–' }}
          <div class="muted small">
            {{ m.model ?? m.boardName ?? '' }}<template v-if="m.serialNumber"> · {{ m.serialNumber }}</template>
          </div>
        </dd>
      </div>
      <div>
        <dt>Uptime</dt>
        <dd>
          {{ uptime ?? '–' }}
          <div v-if="m.bootedAt" class="muted small">since <RelativeTime :value="m.bootedAt" /></div>
        </dd>
      </div>
      <div v-if="r">
        <dt>CPU</dt>
        <dd>
          {{ r.cpuLoad != null ? `${r.cpuLoad} %` : '–' }}
          <div class="muted small">{{ r.cpuCount }} × {{ r.cpu }}</div>
        </dd>
      </div>
      <div v-if="r">
        <dt>Memory</dt>
        <dd>
          {{ formatPercent(used(r.freeMemory, r.totalMemory), 0) }} used
          <div class="muted small">{{ formatBytes(r.freeMemory) }} free of {{ formatBytes(r.totalMemory) }}</div>
        </dd>
      </div>
      <div v-if="r">
        <dt>Storage</dt>
        <dd>
          {{ formatPercent(used(r.freeStorage, r.totalStorage), 0) }} used
          <div class="muted small">{{ formatBytes(r.freeStorage) }} free of {{ formatBytes(r.totalStorage) }}</div>
        </dd>
      </div>
      <div v-if="m.firmwareCurrent">
        <dt>Firmware</dt>
        <dd>
          <span class="mono">{{ m.firmwareCurrent }}</span>
          <div v-if="m.firmwareUpgrade && m.firmwareUpgrade !== m.firmwareCurrent" class="small update">
            {{ m.firmwareUpgrade }} waits for a reboot
          </div>
        </dd>
      </div>
      <div v-if="m.mode !== 'WireGuardOnly'">
        <dt>Last backup</dt>
        <dd>
          <RouterLink v-if="m.latestBackup" :to="{ name: 'sensor', params: { id: sensor.id, tab: 'backups' } }"
            >v{{ m.latestBackup.number }}</RouterLink
          >
          <span v-else>–</span>
          <div class="muted small" :class="{ bad: m.backupFailures > 0 }">
            <template v-if="m.backupFailures > 0">export failed</template>
            <template v-else-if="m.lastExportAt">checked <RelativeTime :value="m.lastExportAt" /></template>
          </div>
        </dd>
      </div>
      <div>
        <dt>Last poll</dt>
        <dd>
          <RelativeTime :value="m.lastPollAt" />
          <div class="muted small">
            {{ modeLabel(m.mode)
            }}<template v-if="m.address">
              · <span class="mono">{{ m.address }}</span></template
            >
          </div>
        </dd>
      </div>
    </dl>
    <Message v-if="m.lastError" severity="error" :closable="false" size="small">
      {{ m.lastError }}<template v-if="m.failingSince"> · since <RelativeTime :value="m.failingSince" /></template>
    </Message>
    <div v-if="m.warnings.length || m.scriptOutdated" class="warnings">
      <Message v-for="w in m.warnings" :key="w" severity="warn" :closable="false" size="small">{{ w }}</Message>
      <Message v-if="m.scriptOutdated" severity="warn" :closable="false" size="small">
        The router's setup differs from its join script:
        <RouterLink :to="{ name: 'sensor', params: { id: sensor.id, tab: 'setup' } }">paste the new script</RouterLink>.
      </Message>
    </div>
  </section>
</template>

<style scoped>
.strip {
  padding: var(--pnmt-space-2) var(--pnmt-space-4);
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.stats {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2) var(--pnmt-space-6);
  margin: 0;
}

.stats dt {
  font-size: 0.786rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pnmt-muted);
  font-weight: 600;
}

.stats dd {
  margin: 0;
  font-weight: 500;
}

.small {
  font-size: 0.857rem;
  font-weight: 400;
}

.update {
  color: var(--pnmt-warn);
}

.bad {
  color: var(--pnmt-fail);
}

.warnings {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

@media (max-width: 599px) {
  .strip {
    padding: var(--pnmt-space-2) var(--pnmt-space-3);
  }

  .stats {
    gap: var(--pnmt-space-2) var(--pnmt-space-4);
  }
}
</style>
