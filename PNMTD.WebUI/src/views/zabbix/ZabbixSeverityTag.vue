<script setup lang="ts">
import type { S } from '@/api/resources'
import { severityLabel } from './zabbix'

/** A severity in Zabbix's colours (docs/audit-redmine-zabbix-plan.md §7.4), which people who use Zabbix know. */
defineProps<{ severity: S['ZabbixSeverity'] }>()
</script>

<template>
  <span class="sev" :class="`sev-${severity}`">{{ severityLabel(severity) }}</span>
</template>

<style scoped>
.sev {
  display: inline-flex;
  align-items: center;
  padding: 0 0.5em;
  border-radius: var(--p-border-radius-sm, 4px);
  font-size: 0.8em;
  font-weight: 600;
  line-height: 1.6;
  white-space: nowrap;
  color: #111;
}
</style>

<style>
/* Zabbix's colours, shared with the overview's matrix. */
.sev-Disaster {
  background: #e45959;
}
.sev-High {
  background: #e97659;
}
.sev-Average {
  background: #ffa059;
}
.sev-Warning {
  background: #ffc859;
}
.sev-Information {
  background: #7499ff;
}
.sev-NotClassified {
  background: #97aab3;
}
</style>
