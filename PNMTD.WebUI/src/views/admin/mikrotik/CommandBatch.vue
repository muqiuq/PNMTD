<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'

/**
 * A command batch's results grouped by outcome and output (docs/mikrotik-plan.md §8.3): fifty routers that
 * say the same are one group, and the few that differ stand out.
 */
const props = defineProps<{ batchId: string }>()
const emit = defineEmits<{ close: [] }>()
const q = useQuery({
  queryKey: computed(() => [...keys.mikrotik, 'commands', 'batch', props.batchId]),
  queryFn: () => api.mikrotik.commandBatch(props.batchId),
  refetchInterval: (query) =>
    query.state.data?.groups.some((g) => g.state === 'Queued' || g.state === 'Running') ? 1500 : false,
})
const routers = computed(() => q.data.value?.groups.reduce((n, g) => n + g.jobs.length, 0) ?? 0)
const severity = (s: S['MikrotikJobState']) =>
  s === 'Succeeded'
    ? 'success'
    : s === 'Failed'
      ? 'danger'
      : s === 'TimedOut'
        ? 'warn'
        : s === 'Running' || s === 'Queued'
          ? 'info'
          : 'secondary'
</script>

<template>
  <section v-if="q.data.value" class="panel" aria-labelledby="batch-title">
    <div id="batch-title" class="panel-header head">
      <span>Command on {{ routers }} router(s)</span>
      <span class="muted small"
        >as {{ q.data.value.runAs }} · by {{ q.data.value.requestedBy }} ·
        <RelativeTime :value="q.data.value.createdAt"
      /></span>
      <span class="spacer" />
      <Button icon="pi pi-times" text size="small" aria-label="Close the batch" @click="emit('close')" />
    </div>
    <div class="panel-body body">
      <pre v-if="q.data.value.command" class="text" aria-label="Command">{{ q.data.value.command }}</pre>
      <p v-else class="muted">The text was redacted.</p>
      <div v-for="(g, i) in q.data.value.groups" :key="i" class="group">
        <div class="group-head">
          <Tag :value="g.state === 'TimedOut' ? 'Timed out' : g.state" :severity="severity(g.state)" />
          <strong>{{ g.jobs.length }}</strong>
          <span class="names">
            <template v-for="(j, k) in g.jobs" :key="j.id">
              <RouterLink v-if="j.sensorId" :to="{ name: 'sensor', params: { id: j.sensorId, tab: 'commands' } }">{{
                j.routerName
              }}</RouterLink>
              <span v-else>{{ j.routerName }}</span>
              <span v-if="k < g.jobs.length - 1">, </span>
            </template>
          </span>
        </div>
        <p v-if="g.jobs[0]?.error" class="bad note">{{ g.jobs[0].error }}</p>
        <pre v-if="g.output" class="output">{{ g.output }}</pre>
        <small v-if="g.outputTruncated" class="muted">The output was cut.</small>
      </div>
    </div>
  </section>
</template>

<style scoped>
.head {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.spacer {
  flex: 1;
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.group {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.group-head {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.text,
.output {
  margin: 0;
  padding: var(--pnmt-space-2);
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  white-space: pre;
  overflow: auto;
  max-height: 20rem;
}

.note {
  margin: 0;
}

.bad {
  color: var(--pnmt-fail);
}
</style>
