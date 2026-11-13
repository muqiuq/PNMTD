<script setup lang="ts">
import type { S } from '@/api/resources'
import { formatAbsolute, formatTime } from '@/format'

/** A job's steps as they happened (docs/mikrotik-plan.md §7.3), with the router's answers. */
defineProps<{ log: S['MikrotikJobLogItem'][] }>()
const icon = (o: S['MikrotikJobOutcome']) =>
  o === 'Ok'
    ? 'pi pi-check-circle ok'
    : o === 'Failed'
      ? 'pi pi-times-circle bad'
      : o === 'Started'
        ? 'pi pi-play muted'
        : 'pi pi-info-circle muted'
</script>

<template>
  <ol class="log">
    <li v-for="l in log" :key="l.id" :class="l.outcome.toLowerCase()">
      <i :class="icon(l.outcome)" aria-hidden="true" />
      <span class="when muted" :title="formatAbsolute(l.at)">{{ formatTime(l.at) }}</span>
      <span class="step">{{ l.step }}</span>
      <span class="message">{{ l.message }}</span>
      <pre v-if="l.output" class="output">{{ l.output }}</pre>
    </li>
  </ol>
</template>

<style scoped>
.log {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
  font-size: 0.857rem;
}

li {
  display: grid;
  grid-template-columns: 1.25rem max-content 7rem 1fr;
  gap: var(--pnmt-space-2);
  align-items: baseline;
}

.when {
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

.step {
  font-weight: 600;
}

.ok {
  color: var(--pnmt-ok);
}

.bad,
.failed .message {
  color: var(--pnmt-fail);
}

.output {
  grid-column: 2 / -1;
  margin: 0;
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  font-family: var(--pnmt-mono);
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-sm);
  white-space: pre-wrap;
  max-height: 12rem;
  overflow: auto;
}
</style>
