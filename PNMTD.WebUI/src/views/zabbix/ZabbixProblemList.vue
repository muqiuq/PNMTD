<script setup lang="ts">
import type { S } from '@/api/resources'
import SinceText from '@/components/SinceText.vue'
import SensorTicket from '@/views/redmine/SensorTicket.vue'
import ZabbixSeverityTag from './ZabbixSeverityTag.vue'

/**
 * A short list of open problems (§7.5, §7.6, §7.7): severity, name and operational data, since when, acknowledged, and
 * the problem in Zabbix. On a sensor's page each says whether the sensor's rules count it.
 */
defineProps<{ problems: S['ZabbixProblemItem'][]; counted?: boolean[] }>()
</script>

<template>
  <p v-if="!problems.length" class="muted none">No open problems.</p>
  <ul v-else class="problems">
    <li v-for="(p, i) in problems" :key="p.id" :class="{ uncounted: counted && !counted[i] }">
      <ZabbixSeverityTag :severity="p.severity" />
      <span class="main">
        <span class="name">{{ p.name }}</span>
        <span class="meta muted">
          <template v-if="p.operationalData">{{ p.operationalData }} · </template>since <SinceText :since="p.since" />
          <template v-if="p.acknowledged">
            · <i class="pi pi-check-circle" title="Acknowledged" /> acknowledged</template
          >
          <template v-if="p.suppressed"> · suppressed (maintenance)</template>
          <template v-if="counted && !counted[i]"> · not counted by this sensor</template>
        </span>
      </span>
      <SensorTicket v-if="p.sensorId" :sensor-id="p.sensorId" />
      <a
        :href="p.url"
        target="_blank"
        rel="noopener noreferrer"
        class="open"
        :aria-label="`${p.name} in Zabbix`"
        title="Open in Zabbix"
      >
        <i class="pi pi-external-link" />
      </a>
    </li>
  </ul>
</template>

<style scoped>
.none {
  margin: 0;
}

.problems {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.problems li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.uncounted {
  opacity: 0.65;
}

.main {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.meta {
  font-size: 0.85em;
}

.open {
  flex-shrink: 0;
}
</style>
