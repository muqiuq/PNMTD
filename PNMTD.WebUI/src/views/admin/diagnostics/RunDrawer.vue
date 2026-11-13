<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Drawer from 'primevue/drawer'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { outcomeLabel, outcomeSeverity } from './diagnosticsFormat'

/** A run with each check's result and values (docs/windows-sensor-plan.md §5.4); refreshed until its result arrived. */
const props = defineProps<{ runId: string | null }>()
const emit = defineEmits<{ close: [] }>()
const open = computed({ get: () => props.runId !== null, set: (v) => !v && emit('close') })
const detail = useQuery({
  queryKey: computed(() => [...keys.diagnostics, 'run', props.runId]),
  queryFn: () => api.diagnostics.runDetail(props.runId!),
  enabled: computed(() => props.runId !== null),
  refetchInterval: (q) => (q.state.data?.run.outcome ? false : 2000),
})
</script>

<template>
  <Drawer v-model:visible="open" position="right" header="Diagnostics run" class="run-drawer">
    <template v-if="detail.data.value">
      <p class="line">
        <Tag
          :value="outcomeLabel(detail.data.value.run.outcome)"
          :severity="outcomeSeverity(detail.data.value.run.outcome)"
        />
        <strong>{{ detail.data.value.run.setName }}</strong> on {{ detail.data.value.run.target.name }}
      </p>
      <p class="muted small">
        {{ detail.data.value.run.trigger
        }}<template v-if="detail.data.value.run.requestedBy"> by {{ detail.data.value.run.requestedBy }}</template> ·
        <RelativeTime :value="detail.data.value.run.startedAt" />
      </p>
      <p>{{ detail.data.value.run.summary ?? 'Waiting for the result…' }}</p>
      <section v-for="r in detail.data.value.results" :key="r.checkId" class="result">
        <div class="line">
          <Tag :value="outcomeLabel(r.outcome)" :severity="outcomeSeverity(r.outcome)" />
          <strong>{{ r.name }}</strong> <span class="muted small">{{ r.type }} · {{ r.durationMs }} ms</span>
        </div>
        <div>{{ r.message }}</div>
        <div v-if="r.ping" class="small muted mono">
          {{ r.ping.address }}: {{ r.ping.received }}/{{ r.ping.sent }}, min {{ r.ping.minMs ?? '–' }} / avg
          {{ r.ping.avgMs ?? '–' }} / max {{ r.ping.maxMs ?? '–' }} ms
        </div>
        <div v-if="r.dns" class="small muted mono">
          {{ r.dns.resolver }}: {{ r.dns.answers.join(', ') || r.dns.responseCode }} ({{ r.dns.ms }} ms)
        </div>
      </section>
    </template>
  </Drawer>
</template>

<style scoped>
.line {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: center;
  flex-wrap: wrap;
}

.result {
  border-top: 1px solid var(--pnmt-border);
  padding: var(--pnmt-space-2) 0;
}

.small {
  font-size: 0.857rem;
}
</style>

<style>
.run-drawer {
  width: min(36rem, 100vw);
}
</style>
