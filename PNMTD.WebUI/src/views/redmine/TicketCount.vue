<script setup lang="ts">
import { computed } from 'vue'
import type { RedmineTarget } from '@/api/resources'
import { useRelated } from './useRelated'

/** The header's chip (§6.6): "2 open tickets", scrolling to the Tickets panel. Nothing while there are none. */
const props = defineProps<{ target: RedmineTarget }>()
const { openCount } = useRelated(computed(() => props.target))

function scroll() {
  document.getElementById('tickets')?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}
</script>

<template>
  <a v-if="openCount" href="#tickets" class="ticket-count" @click.prevent="scroll">
    <i class="pi pi-ticket" aria-hidden="true" /> {{ openCount }} open ticket{{ openCount === 1 ? '' : 's' }}
  </a>
</template>

<style scoped>
.ticket-count {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  padding: 0 var(--pnmt-space-2);
  border-radius: 999px;
  border: 1px solid var(--pnmt-border);
  font-size: 0.85em;
  white-space: nowrap;
}
</style>
