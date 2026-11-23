<script setup lang="ts">
import { since } from '../time'
import type { BoardElementData } from '../types'

/** PNMT's uplink (§7.1): up or down, and since when it is down. */
defineProps<{ data: BoardElementData; now: number }>()
</script>

<template>
  <p v-if="data.uplink && !data.uplink.configured" class="board-muted">The uplink check isn't set up.</p>
  <div v-else-if="data.uplink" class="board-tile" :data-tone="data.uplink.up ? 'ok' : 'fail'">
    <span class="board-tile-value">{{ data.uplink.up ? 'Up' : 'Down' }}</span>
    <span v-if="!data.uplink.up && data.uplink.since" class="board-tile-label"
      >for {{ since(data.uplink.since, now) }}</span
    >
  </div>
</template>
