<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import CommandForm from './CommandForm.vue'
import CommandResult from './CommandResult.vue'

/**
 * A router's remote commands (docs/mikrotik-plan.md §8, §11.1): the editor, the result of the last run, and the
 * router's history (admin runs for Admins only), each with its output and Run again.
 */
const props = defineProps<{ sensor: S['SensorDetail']; isAdmin: boolean }>()
const text = ref('')
const current = ref<string | null>(null)
const open = ref<Record<string, boolean>>({})
const history = useQuery({
  queryKey: computed(() => [...keys.mikrotik, 'commands', 'router', props.sensor.id]),
  queryFn: () => api.mikrotik.commands({ sensorId: props.sensor.id, pageSize: 25 }),
  refetchInterval: (q) =>
    q.state.data?.items.some((j) => j.state === 'Queued' || j.state === 'Running') ? 2000 : 30_000,
})
const form = ref<HTMLElement | null>(null)

function started(created: S['MikrotikCommandsCreated']) {
  current.value = created.jobs[0]?.id ?? null
}

function again(command: string) {
  text.value = command
  form.value?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

const firstLine = (c: string) => c.split('\n')[0] + (c.includes('\n') ? ' …' : '')
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
  <div class="tab">
    <section ref="form" class="panel" aria-labelledby="command-title">
      <div id="command-title" class="panel-header">Run a command</div>
      <div class="panel-body body">
        <CommandForm
          v-model:text="text"
          :routers="[{ id: sensor.id, name: sensor.name }]"
          :is-admin="isAdmin"
          @started="started"
        />
        <CommandResult v-if="current" :key="current" :job-id="current" :is-admin="isAdmin" @again="again" />
      </div>
    </section>

    <section class="panel" aria-labelledby="history-title">
      <div id="history-title" class="panel-header">History</div>
      <div class="panel-body">
        <p v-if="!history.data.value?.items.length" class="muted none">No command yet.</p>
        <ul class="history">
          <li v-for="j in history.data.value?.items ?? []" :key="j.id">
            <button
              type="button"
              class="row"
              :aria-expanded="!!open[j.id]"
              @click="open = { ...open, [j.id]: !open[j.id] }"
            >
              <Tag :value="j.state === 'TimedOut' ? 'Timed out' : j.state" :severity="severity(j.state)" />
              <code v-if="j.command" class="ellipsis">{{ firstLine(j.command) }}</code>
              <span v-else class="muted">redacted</span>
              <span class="muted"
                >as {{ j.runAs }} · by {{ j.requestedBy }} · <RelativeTime :value="j.createdAt"
              /></span>
            </button>
            <CommandResult
              v-if="open[j.id]"
              :job-id="j.id"
              :is-admin="isAdmin"
              show-text
              class="expanded"
              @again="again"
            />
          </li>
        </ul>
      </div>
    </section>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-4);
}

.none {
  margin: 0;
}

.history {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.row {
  all: unset;
  cursor: pointer;
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  max-width: 100%;
}

.row:focus-visible {
  outline: 2px solid var(--p-primary-color);
}

code {
  font-family: var(--pnmt-mono);
  min-width: 0;
}

.expanded {
  margin: var(--pnmt-space-2) 0 var(--pnmt-space-3) var(--pnmt-space-4);
}
</style>
