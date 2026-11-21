<script setup lang="ts">
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed, ref, toRef } from 'vue'
import { api, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { useActions } from '@/composables/useActions'
import { useNow } from '@/composables/useNow'
import AptLog from './AptLog.vue'
import HighRiskPanel from './HighRiskPanel.vue'
import InstallUpdatesDialog from './InstallUpdatesDialog.vue'
import OsUpdateSettings from './OsUpdateSettings.vue'
import OsUpdatesStrip from './OsUpdatesStrip.vue'
import PendingPackages from './PendingPackages.vue'
import UpdateRuns from './UpdateRuns.vue'
import { runDuration, runWhat, triggerLabel } from './osUpdatesFormat'
import { osUpdatesInvalidate, useOsUpdates } from './useOsUpdates'

/**
 * A Linux server's OS updates tab (docs/linux-os-updates-plan.md §10.1): the state with *Refresh lists* and *Install updates…*,
 * the run in progress with its live log, the high-risk findings with their acknowledgements, the pending packages, the log of
 * runs, and for Servers: Manage the server's settings. Viewing needs Monitoring: View, the buttons Servers: Run (D2).
 */
const props = defineProps<{ server: S['AgentServerDetail']; canRun: boolean; canManage: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const { query, view, set } = useOsUpdates(id, ref(true))
const { run, invalidate, notify } = useActions()
const now = useNow()

const refreshing = ref(false)
async function refresh() {
  refreshing.value = true
  try {
    const started = await run(() => api.linux.osUpdates.update(id.value), {
      failure: 'Not started',
      invalidate: osUpdatesInvalidate(id.value),
    })
    if (!started) return
    if (started.state === 'Refused' || started.state === 'Lost') {
      notify.error(new Error(started.message ?? "The server didn't run it."), 'Not started')
    } else {
      notify.success('apt-get update started', 'Its log follows below.')
    }
  } finally {
    refreshing.value = false
  }
}
const installing = ref(false)
const current = computed(() => view.value?.current ?? null)
/** The run ended: the new state, the runs, the sensor and the card at once. */
const finished = () => invalidate(osUpdatesInvalidate(id.value))
</script>

<template>
  <div class="tab">
    <ErrorState
      v-if="query.isError.value && !view"
      :error="query.error.value"
      what="the OS updates"
      @retry="query.refetch()"
    />
    <div v-else-if="!view" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <template v-else>
      <OsUpdatesStrip
        :view="view"
        :can-run="canRun"
        :refreshing="refreshing"
        @refresh="refresh"
        @install="installing = true"
      />
      <section v-if="current" class="panel" aria-labelledby="current-run-title">
        <div class="panel-header">
          <span id="current-run-title">{{ current.state === 'Requested' ? 'Asked for' : 'Running now' }}</span>
          <Tag :value="runWhat(current)" severity="info" />
          <span class="normal small"
            >{{ current.trigger === 'Person' ? 'by ' : '' }}{{ triggerLabel(current) }} ·
            {{ current.state === 'Requested' ? "waiting for the server's answer" : runDuration(current, now) }}</span
          >
          <Tag
            v-if="current.takingLong"
            value="taking long"
            severity="warn"
            :title="`Longer than ${view.upgradeWarnMinutes} minutes. An upgrade is never stopped: killing dpkg would leave the system half-configured.`"
          />
          <span v-if="current.waiting || !view.connected" class="muted normal small"
            >waiting for the server: it reports when it is back</span
          >
        </div>
        <div class="panel-body">
          <AptLog :key="current.id" :sensor-id="view.sensorId" :run="current" @finished="finished" />
        </div>
      </section>
      <HighRiskPanel v-if="view.checkEnabled" :view="view" :can-run="canRun" @changed="set" />
      <PendingPackages :view="view" />
      <UpdateRuns :sensor-id="view.sensorId" :can-run="canRun" />
      <OsUpdateSettings v-if="canManage" :view="view" @changed="set" />
      <InstallUpdatesDialog v-if="canRun" v-model:visible="installing" :view="view" @started="finished" />
    </template>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}

.normal {
  font-weight: 400;
}

.small {
  font-size: 0.857rem;
}
</style>
