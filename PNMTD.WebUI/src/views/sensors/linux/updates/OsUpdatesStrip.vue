<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed, toRef } from 'vue'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useLinuxSection } from '../../agent/useAgentSection'
import {
  installBlocked,
  kernelText,
  needsRestartText,
  pendingText,
  refreshBlocked,
  runStateLabel,
  runStateSeverity,
  runWhat,
  triggerLabel,
} from './osUpdatesFormat'

/**
 * A server's OS updates at a glance (docs/linux-os-updates-plan.md §10.1 1): the pending updates with the security and high-risk
 * ones, the lists' age and the next daily update, the last upgrade, a pending reboot, unattended-upgrades, and the services
 * needrestart says still run old libraries, only reported (D4). *Refresh lists* and *Install updates…* for Servers: Run; a
 * disabled one says why.
 */
const props = defineProps<{ view: S['OsUpdatesView']; canRun: boolean; refreshing: boolean }>()
const emit = defineEmits<{ refresh: []; install: [] }>()
const id = toRef(() => props.view.sensorId)
// unattended-upgrades is the system section's (docs/linux-sensor-plan.md §5.2), shared with the overview.
const system = useLinuxSection(id, 'system')
const updates = computed(() => system.data.value?.system?.updates ?? null)
const unattended = computed(() =>
  !updates.value
    ? null
    : !updates.value.unattendedInstalled
      ? 'not installed'
      : updates.value.unattendedEnabled
        ? 'on'
        : 'installed, off',
)
const time = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' })
const nextAt = computed(() => (props.view.nextUpdateAt ? time.format(new Date(props.view.nextUpdateAt)) : null))
const refreshReason = computed(() => refreshBlocked(props.view))
const installReason = computed(() => installBlocked(props.view))
const restart = computed(() => needsRestartText(props.view.needsRestart))
const kernel = computed(() => kernelText(props.view.needsRestart))
const last = computed(() => props.view.lastUpgrade)
</script>

<template>
  <section class="panel" aria-labelledby="os-updates-state">
    <div class="panel-header">
      <span id="os-updates-state">OS updates</span>
      <span v-if="view.distribution" class="muted normal small mono">{{ view.distribution }}</span>
      <span class="spacer" />
      <template v-if="canRun">
        <Button
          label="Refresh lists"
          icon="pi pi-refresh"
          size="small"
          severity="secondary"
          :disabled="!!refreshReason"
          :loading="refreshing"
          :title="refreshReason ?? 'apt-get update now'"
          @click="emit('refresh')"
        />
        <Button
          label="Install updates…"
          icon="pi pi-download"
          size="small"
          :disabled="!!installReason"
          :title="installReason ?? undefined"
          @click="emit('install')"
        />
      </template>
    </div>
    <div class="panel-body body">
      <Message v-if="view.unavailable" severity="secondary" :closable="false" size="small">{{
        view.unavailable
      }}</Message>
      <template v-else-if="canRun && (refreshReason || installReason)">
        <p v-if="refreshReason === installReason" class="small muted reason">{{ refreshReason }}</p>
        <template v-else>
          <p v-if="refreshReason" class="small muted reason">Refresh lists: {{ refreshReason }}</p>
          <p v-if="installReason" class="small muted reason">Install updates: {{ installReason }}</p>
        </template>
      </template>
      <Message v-if="view.dpkgInterrupted" severity="error" :closable="false" size="small">
        dpkg was interrupted: run <code>dpkg --configure -a</code> on the server. PNMT doesn't repair it.
      </Message>
      <Message v-if="view.updateFailedMessage" severity="error" :closable="false" size="small">{{
        view.updateFailedMessage
      }}</Message>
      <dl class="facts">
        <dt>Pending</dt>
        <dd class="line">
          <span>{{ pendingText(view) }}</span>
          <Tag v-if="view.highRisk" :value="`${view.highRisk} high risk`" severity="danger" />
          <Tag
            v-if="view.highRiskAcknowledged"
            :value="`${view.highRiskAcknowledged} acknowledged`"
            severity="secondary"
          />
          <Tag v-if="view.important" :value="`${view.important} important`" severity="warn" />
        </dd>
        <dt>Lists</dt>
        <dd>
          <template v-if="view.listsUpdatedAt">refreshed <RelativeTime :value="view.listsUpdatedAt" /></template>
          <template v-else>never refreshed by PNMT yet</template
          ><template v-if="view.enabled && view.dailyUpdate && nextAt">, next at about {{ nextAt }}</template>
          <div class="small muted">
            <template v-if="!view.dailyUpdate">No daily update (OsUpdates__DailyUpdate=false). </template
            ><template v-if="unattended">unattended-upgrades {{ unattended }}</template>
          </div>
        </dd>
        <dt>Last upgrade</dt>
        <dd class="line">
          <template v-if="last">
            <RelativeTime :value="last.finishedAt ?? last.startedAt ?? last.requestedAt" />
            <span>{{ runWhat(last) }}, {{ last.trigger === 'Person' ? 'by ' : '' }}{{ triggerLabel(last) }}</span>
            <Tag :value="runStateLabel(last.state)" :severity="runStateSeverity(last.state)" />
            <Tag v-if="last.dismissed" value="marked handled" severity="secondary" />
          </template>
          <span v-else class="muted">none seen yet</span>
        </dd>
        <template v-if="view.rebootRequired || kernel">
          <dt>Reboot</dt>
          <dd>
            <Tag value="reboot pending" severity="warn" />
            <span v-if="view.rebootPackages.length" class="small muted"> for {{ view.rebootPackages.join(', ') }}</span>
            <div v-if="kernel" class="small muted">{{ kernel }}</div>
          </dd>
        </template>
        <template v-if="restart">
          <dt>Services</dt>
          <dd>
            <span class="line">
              <span>{{ restart }}</span>
              <Tag value="report only" severity="secondary" />
            </span>
            <div class="small muted">
              needrestart's list after the last upgrade: PNMT restarts nothing. It clears once the server no longer
              lists them.
            </div>
          </dd>
        </template>
      </dl>
      <Message v-for="w in view.warnings" :key="w" severity="warn" :closable="false" size="small">{{ w }}</Message>
      <p v-if="view.phasedDeferred.length" class="small muted reason">
        Phased updates apt holds back for now: {{ view.phasedDeferred.join(', ') }}
      </p>
    </div>
  </section>
</template>

<style scoped>
.spacer {
  flex: 1;
}

.normal {
  font-weight: 400;
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
}

.reason {
  margin: 0;
}

.small {
  font-size: 0.857rem;
}
</style>
