<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import CommandResult from '@/views/sensors/mikrotik/CommandResult.vue'
import JobLog from '@/views/sensors/mikrotik/JobLog.vue'
import CommandBatch from './CommandBatch.vue'

/**
 * Every update and command job (docs/mikrotik-plan.md §7, §8, §11.2), newest first: an update with its log, a
 * command with its output; a command batch grouped by output (?batch=…). Cancel, Run again, Redact.
 */
const route = useRoute()
const router = useRouter()
const { run, done, ask } = useActions()
const jobs = useQuery({
  queryKey: [...keys.mikrotik, 'all-jobs'],
  queryFn: () => api.mikrotik.allJobs({ pageSize: 100 }),
  refetchInterval: (q) =>
    q.state.data?.items.some((j) => j.state === 'Queued' || j.state === 'Running') ? 3000 : 30_000,
})
const batch = computed(() => (typeof route.query.batch === 'string' ? route.query.batch : null))
const showBatch = (id: string | null) => router.replace({ query: { ...route.query, batch: id ?? undefined } })
const open = ref<Record<string, S['MikrotikJobDetail'] | true | null>>({})
async function toggle(job: S['MikrotikJobItem']) {
  if (open.value[job.id]) {
    open.value = { ...open.value, [job.id]: null }
    return
  }
  if (job.kind === 'Command') {
    open.value = { ...open.value, [job.id]: true }
    return
  }
  const detail = await run(() => api.mikrotik.anyJob(job.id), { failure: 'No log' })
  if (detail) open.value = { ...open.value, [job.id]: detail }
}
async function cancel(job: S['MikrotikJobItem']) {
  if (job.kind === 'Command') {
    if (
      await ask(`Cancel the command on ${job.routerName}?`, 'What the router did already stays done.', 'Cancel command')
    ) {
      await done(() => api.mikrotik.cancelCommand(job.id), { success: 'Cancelled', invalidate: [keys.mikrotik] })
    }
    return
  }
  if (
    await ask(
      `Cancel the update of ${job.routerName}?`,
      'Possible until its reboot. What the router did already stays done.',
      'Cancel update',
    )
  ) {
    await done(() => api.mikrotik.cancelJob(job.id), { success: 'Cancelled', invalidate: [keys.mikrotik] })
  }
}
const cancellable = (j: S['MikrotikJobItem']) =>
  j.state === 'Queued' ||
  (j.state === 'Running' &&
    (j.kind === 'Command'
      ? ['Check', 'Export', 'Run'].includes(j.step)
      : ['Check', 'Export', 'BinaryBackup', 'Download'].includes(j.step)))
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
const detailOf = (id: string) => {
  const d = open.value[id]
  return d && d !== true ? d : null
}
</script>

<template>
  <div class="tab">
    <CommandBatch v-if="batch" :key="batch" :batch-id="batch" @close="showBatch(null)" />
    <section class="panel">
      <div class="panel-body">
        <p v-if="!jobs.data.value?.items.length" class="muted none">
          No job yet. Updates start from a router's Updates tab, commands from its Commands tab or from the Routers
          list.
        </p>
        <ul class="jobs">
          <li v-for="j in jobs.data.value?.items ?? []" :key="j.id">
            <div class="line">
              <button type="button" class="row" :aria-expanded="!!open[j.id]" @click="toggle(j)">
                <Tag :value="j.state === 'TimedOut' ? 'Timed out' : j.state" :severity="severity(j.state)" />
                <RouterLink
                  v-if="j.sensorId"
                  :to="{
                    name: 'sensor',
                    params: { id: j.sensorId, tab: j.kind === 'Command' ? 'commands' : 'updates' },
                  }"
                  @click.stop
                  >{{ j.routerName }}</RouterLink
                >
                <span v-else>{{ j.routerName }}</span>
                <template v-if="j.kind === 'Command'">
                  <code v-if="j.command" class="ellipsis">{{ firstLine(j.command) }}</code>
                  <span v-else class="muted">redacted</span>
                  <span class="muted">as {{ j.runAs }}</span>
                </template>
                <span v-else class="mono">{{ j.fromVersion ?? '?' }} → {{ j.toVersion }}</span>
                <span class="muted">by {{ j.requestedBy }} · <RelativeTime :value="j.createdAt" /></span>
                <span v-if="j.state === 'Running'" class="muted">· {{ j.step }}</span>
                <span v-if="j.error && j.kind === 'Update'" class="bad ellipsis">{{ j.error }}</span>
              </button>
              <Button
                v-if="j.kind === 'Command'"
                label="Batch"
                size="small"
                text
                severity="secondary"
                @click="showBatch(j.batchId)"
              />
              <Button v-if="cancellable(j)" label="Cancel" size="small" text severity="danger" @click="cancel(j)" />
            </div>
            <CommandResult v-if="open[j.id] && j.kind === 'Command'" :job-id="j.id" is-admin show-text class="detail" />
            <JobLog v-else-if="detailOf(j.id)" :log="detailOf(j.id)!.log" class="detail" />
          </li>
        </ul>
      </div>
    </section>
  </div>
</template>

<style scoped>
.none {
  margin: 0;
}

.jobs {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.row {
  all: unset;
  cursor: pointer;
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
  flex: 1;
}

.mono {
  font-family: var(--pnmt-mono);
}

.bad {
  color: var(--pnmt-fail);
}

.detail {
  margin: var(--pnmt-space-2) 0 var(--pnmt-space-3) var(--pnmt-space-4);
}

.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

code {
  font-family: var(--pnmt-mono);
  min-width: 0;
}
</style>
