<script setup lang="ts">
import type { S } from '@/api/resources'
import { safeLink } from './osUpdatesFormat'

/**
 * A finding's reasons (docs/linux-os-updates-plan.md §6.4): each with its source and evidence, the ones that make it high risk
 * marked, and the source's link when it is http(s). The texts come from outside sources: shown as text only.
 */
defineProps<{ reasons: S['OsFindingReason'][] }>()
</script>

<template>
  <ul class="finding-reasons">
    <li v-for="(r, i) in reasons" :key="i" :class="{ high: r.high }">
      <i :class="r.high ? 'pi pi-exclamation-circle' : 'pi pi-info-circle'" aria-hidden="true" />
      <span class="source">{{ r.source }}</span>: {{ r.detail
      }}<a v-if="safeLink(r.link)" :href="safeLink(r.link)!" target="_blank" rel="noopener noreferrer" class="link"
        >source <i class="pi pi-external-link" aria-hidden="true"
      /></a>
    </li>
  </ul>
</template>

<style scoped>
.finding-reasons {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
  font-size: 0.857rem;
}

li {
  display: block;
  overflow-wrap: anywhere;
}

li > i {
  margin-right: var(--pnmt-space-1);
  font-size: 0.786rem;
  color: var(--pnmt-muted);
}

.high > i {
  color: var(--pnmt-fail);
}

.source {
  font-weight: 600;
}

.link {
  margin-left: var(--pnmt-space-2);
  white-space: nowrap;
}

.link i {
  font-size: 0.7rem;
}
</style>
