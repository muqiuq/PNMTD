<script setup lang="ts">
import type { S } from '@/api/resources'
import { progressLabel, secondsLabel, stepLabel } from './networkFormat'

/** While a scan runs (§8.1): which router, which step, how many are left; a link to the Scans tab. */
defineProps<{ status: S['NetworkScanStatus'] | undefined }>()
</script>

<template>
  <RouterLink
    v-if="status?.running"
    :to="{ name: 'network-map', query: { tab: 'scans' } }"
    class="scan-strip"
    data-testid="scan-strip"
  >
    <i class="pi pi-spin pi-sync" aria-hidden="true" />
    <span>
      Scanning <strong>{{ status.running.currentRouterName ?? '…' }}</strong>
      <template v-if="status.running.currentStep"> ({{ stepLabel(status.running) }})</template>
      · {{ progressLabel(status.running) }}
      <span v-if="status.running.estimatedSecondsLeft" class="muted">
        · {{ secondsLabel(status.running.estimatedSecondsLeft) }}</span
      >
    </span>
    <span v-if="status.queued.length" class="muted">{{ status.queued.length }} more queued</span>
  </RouterLink>
</template>

<style scoped>
.scan-strip {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border: 1px solid var(--pnmt-border);
  border-radius: 6px;
  background: var(--pnmt-panel);
  color: var(--pnmt-text);
  text-decoration: none;
  font-size: 0.929rem;
  flex-wrap: wrap;
}
</style>
