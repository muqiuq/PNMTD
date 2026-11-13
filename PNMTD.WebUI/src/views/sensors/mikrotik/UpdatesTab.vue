<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import JobLog from './JobLog.vue'
import { fixLabel, fixSeverity, isSecurityLine } from './security'
import UpdateDialog from './UpdateDialog.vue'

/**
 * A router's updates (docs/mikrotik-plan.md §7, §11.1): installed against the newest of its channel, the
 * release's changelog, the firmware, Update… for Admins, and the router's jobs with their logs. A pending security fix
 * (docs/ai-operations-plan.md §10.4) is marked, and the changelog's security lines are highlighted.
 */
const props = defineProps<{ sensor: S['SensorDetail']; isAdmin: boolean }>()
const { run } = useActions()
const m = computed(() => props.sensor.mikrotik!)
const jobs = useQuery({
  queryKey: computed(() => [...keys.mikrotik, 'jobs', props.sensor.id]),
  queryFn: () => api.mikrotik.jobs(props.sensor.id, { pageSize: 50 }),
  refetchInterval: (q) =>
    q.state.data?.items.some((j) => j.state === 'Queued' || j.state === 'Running') ? 3000 : 30_000,
})
const changelog = ref<string | null>(null)
async function showChangelog() {
  const c = await run(() => api.mikrotik.changelog(props.sensor.id, m.value.latestVersion!), {
    failure: 'No changelog',
  })
  if (c) changelog.value = c.text
}
const changelogLines = computed(() =>
  (changelog.value ?? '').split('\n').map((text) => ({ text, security: isSecurityLine(text) })),
)
const open = ref<Record<string, S['MikrotikJobDetail'] | null>>({})
async function toggle(job: S['MikrotikJobItem']) {
  if (open.value[job.id]) {
    open.value = { ...open.value, [job.id]: null }
    return
  }
  const detail = await run(() => api.mikrotik.job(props.sensor.id, job.id), { failure: 'No log' })
  if (detail) open.value = { ...open.value, [job.id]: detail }
}
const updating = ref(false)
const severity = (s: S['MikrotikJobState']) =>
  s === 'Succeeded' ? 'success' : s === 'Failed' ? 'danger' : s === 'Running' || s === 'Queued' ? 'info' : 'secondary'
</script>

<template>
  <div class="tab">
    <section class="panel" aria-labelledby="version-title">
      <div id="version-title" class="panel-header">
        RouterOS
        <span class="spacer" />
        <Button
          v-if="isAdmin"
          label="Update…"
          icon="pi pi-upload"
          size="small"
          :disabled="!m.version"
          @click="updating = true"
        />
      </div>
      <dl class="panel-body facts">
        <dt>Installed</dt>
        <dd>
          <span class="mono">{{ m.version ?? '–' }}</span> <span class="muted">{{ m.channel ?? '' }}</span>
        </dd>
        <dt>Newest of {{ m.channel ?? 'its channel' }}</dt>
        <dd>
          <span class="mono">{{ m.latestVersion ?? '–' }}</span>
          <Tag v-if="m.updateAvailable" value="update available" severity="warn" class="tag" />
          <Tag
            v-if="m.securityFix"
            :value="fixLabel(m.securityFix)"
            :severity="fixSeverity(m.securityFix)"
            icon="pi pi-shield"
            class="tag"
          />
          <Button v-if="m.latestVersion" label="Changelog" size="small" text @click="showChangelog" />
        </dd>
        <template v-if="m.firmwareCurrent">
          <dt>RouterBOARD firmware</dt>
          <dd>
            <span class="mono">{{ m.firmwareCurrent }}</span>
            <span v-if="m.firmwareUpgrade && m.firmwareUpgrade !== m.firmwareCurrent" class="muted">
              · {{ m.firmwareUpgrade }} after a reboot</span
            >
          </dd>
        </template>
      </dl>
      <pre
        v-if="changelog"
        class="changelog"
      ><span v-for="(l, i) in changelogLines" :key="i" :class="{ security: l.security }">{{ l.text }}{{ '\n' }}</span></pre>
    </section>

    <section class="panel" aria-labelledby="jobs-title">
      <div id="jobs-title" class="panel-header">Updates</div>
      <div class="panel-body">
        <p v-if="!jobs.data.value?.items.length" class="muted none">No update yet.</p>
        <ul class="jobs">
          <li v-for="j in jobs.data.value?.items ?? []" :key="j.id">
            <button type="button" class="row" @click="toggle(j)">
              <Tag :value="j.state" :severity="severity(j.state)" />
              <span class="mono">{{ j.fromVersion ?? '?' }} → {{ j.toVersion }}</span>
              <span class="muted"
                >{{ j.channel }} · by {{ j.requestedBy }} as {{ j.adminUsername }} · <RelativeTime :value="j.createdAt"
              /></span>
              <span v-if="j.state === 'Running'" class="muted">· {{ j.step }}</span>
              <span v-if="j.error" class="bad ellipsis">{{ j.error }}</span>
            </button>
            <JobLog v-if="open[j.id]" :log="open[j.id]!.log" class="joblog" />
          </li>
        </ul>
      </div>
    </section>
    <UpdateDialog
      v-if="isAdmin"
      v-model:visible="updating"
      :routers="[
        {
          id: sensor.id,
          name: sensor.name,
          identity: m.identity,
          serial: m.serialNumber,
          from: m.version,
          to: m.latestVersion,
          method: m.updateMethod,
          checkStorage: m.updateCheckStorage,
          lowStorage: m.lowStorage,
          freeStorage: m.resources?.freeStorage,
        },
      ]"
      :channel="m.channel"
    />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.panel-header {
  display: flex;
  align-items: center;
}

.spacer {
  flex: 1;
}

.mono {
  font-family: var(--pnmt-mono);
}

.tag {
  margin-left: var(--pnmt-space-2);
}

.changelog {
  margin: 0 var(--pnmt-space-3) var(--pnmt-space-3);
  padding: var(--pnmt-space-2);
  max-height: 20rem;
  overflow: auto;
  font-size: 0.857rem;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  white-space: pre-wrap;
}

.changelog .security {
  color: var(--pnmt-fail);
  font-weight: 600;
}

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

.row {
  all: unset;
  cursor: pointer;
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.bad {
  color: var(--pnmt-fail);
}

.joblog {
  margin: var(--pnmt-space-2) 0 0 var(--pnmt-space-4);
}
</style>
