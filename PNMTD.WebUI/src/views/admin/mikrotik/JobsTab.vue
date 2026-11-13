<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import { ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import JobLog from '@/views/sensors/mikrotik/JobLog.vue'

/** Every update job (docs/mikrotik-plan.md §7, §11.2), newest first, with its log; cancel until the reboot. */
const { run, done, ask } = useActions()
const jobs = useQuery({
  queryKey: [...keys.mikrotik, 'all-jobs'],
  queryFn: () => api.mikrotik.allJobs({ pageSize: 100 }),
  refetchInterval: (q) =>
    q.state.data?.items.some((j) => j.state === 'Queued' || j.state === 'Running') ? 3000 : 30_000,
})
const open = ref<Record<string, S['MikrotikJobDetail'] | null>>({})
async function toggle(job: S['MikrotikJobItem']) {
  if (open.value[job.id]) {
    open.value = { ...open.value, [job.id]: null }
    return
  }
  const detail = await run(() => api.mikrotik.anyJob(job.id), { failure: 'No log' })
  if (detail) open.value = { ...open.value, [job.id]: detail }
}
async function cancel(job: S['MikrotikJobItem']) {
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
const severity = (s: S['MikrotikJobState']) =>
  s === 'Succeeded' ? 'success' : s === 'Failed' ? 'danger' : s === 'Running' || s === 'Queued' ? 'info' : 'secondary'
</script>

<template>
  <section class="panel">
    <div class="panel-body">
      <p v-if="!jobs.data.value?.items.length" class="muted none">
        No job yet. Updates start from a router's Updates tab.
      </p>
      <ul class="jobs">
        <li v-for="j in jobs.data.value?.items ?? []" :key="j.id">
          <div class="line">
            <button type="button" class="row" @click="toggle(j)">
              <Tag :value="j.state" :severity="severity(j.state)" />
              <RouterLink
                v-if="j.sensorId"
                :to="{ name: 'sensor', params: { id: j.sensorId, tab: 'updates' } }"
                @click.stop
                >{{ j.routerName }}</RouterLink
              >
              <span v-else>{{ j.routerName }}</span>
              <span class="mono">{{ j.fromVersion ?? '?' }} → {{ j.toVersion }}</span>
              <span class="muted">by {{ j.requestedBy }} · <RelativeTime :value="j.createdAt" /></span>
              <span v-if="j.state === 'Running'" class="muted">· {{ j.step }}</span>
              <span v-if="j.error" class="bad ellipsis">{{ j.error }}</span>
            </button>
            <Button
              v-if="
                j.state === 'Queued' ||
                (j.state === 'Running' && ['Check', 'Export', 'BinaryBackup', 'Download'].includes(j.step))
              "
              label="Cancel"
              size="small"
              text
              severity="danger"
              @click="cancel(j)"
            />
          </div>
          <JobLog v-if="open[j.id]" :log="open[j.id]!.log" class="joblog" />
        </li>
      </ul>
    </div>
  </section>
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

.joblog {
  margin: var(--pnmt-space-2) 0 0 var(--pnmt-space-4);
}
</style>
