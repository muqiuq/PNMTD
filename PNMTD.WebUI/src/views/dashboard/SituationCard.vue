<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useSession } from '@/stores/session'
import { situationSeverity } from '@/views/admin/ai/aiFormat'

/**
 * The situation (docs/ai-operations-plan.md §9.3): the AI's latest finished analysis, its status and summary. Hidden
 * until there is one, so an installation without AI shows nothing. The details are on Admin → AI → Analysis.
 */
const session = useSession()
const latest = useQuery({
  queryKey: [...keys.ai, 'analysis', 'latest'],
  queryFn: () => api.ai.analysis({ state: 'Done', pageSize: 1 }),
  refetchInterval: 60_000,
})
const run = computed(() => latest.data.value?.items[0])
</script>

<template>
  <section v-if="run" class="panel situation" aria-labelledby="situation-title">
    <header class="panel-header">
      <span id="situation-title">Situation <span class="muted">(AI)</span></span>
      <RouterLink v-if="session.can('Ai', 'Manage')" :to="{ name: 'ai', params: { tab: 'analysis' } }" class="all"
        >Details <i class="pi pi-angle-right"
      /></RouterLink>
    </header>
    <div class="body">
      <div class="line">
        <Tag v-if="run.status" :value="run.status" :severity="situationSeverity(run.status)" />
        <Tag v-if="run.critical" :value="`${run.critical} critical`" severity="danger" />
        <Tag v-if="run.warnings" :value="`${run.warnings} warn`" severity="warn" />
        <span class="muted small"><RelativeTime :value="run.completedAt ?? run.at" /></span>
      </div>
      <p v-if="run.summary" class="summary">{{ run.summary }}</p>
    </div>
  </section>
</template>

<style scoped>
.all {
  margin-left: auto;
  font-weight: 400;
  font-size: 0.929rem;
}

.body {
  padding: var(--pnmt-space-2) var(--pnmt-space-3) var(--pnmt-space-3);
}

.line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.summary {
  margin: var(--pnmt-space-2) 0 0;
}

.small {
  font-size: 0.857rem;
}
</style>
