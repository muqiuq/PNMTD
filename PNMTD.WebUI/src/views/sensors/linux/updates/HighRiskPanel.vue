<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { api, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { formatAbsolute } from '@/format'
import AcknowledgeFindingsDialog from './AcknowledgeFindingsDialog.vue'
import FindingReasons from './FindingReasons.vue'
import { osUpdatesInvalidate } from './useOsUpdates'

/**
 * The high-risk findings of a server (docs/linux-os-updates-plan.md §10.1 3, §6.4, §6.5), only when there are some: per package
 * installed → candidate and since when it is pending, each CVE with its reasons and the sources' links. An acknowledged one is
 * greyed out with who acknowledged it, why and until when, and *Remove acknowledgement*; for Servers: Run a checkbox per finding
 * and *Acknowledge…*. How earlier acknowledgements ended is in the audit log, on the sensor's History.
 */
const props = defineProps<{ view: S['OsUpdatesView']; canRun: boolean }>()
const emit = defineEmits<{ changed: [view: S['OsUpdatesView']] }>()
const { run, ask } = useActions()

const high = computed(() => props.view.findings.filter((f) => f.rating === 'HighRisk'))
/** The findings by package and candidate, the oldest pending first within each. */
const groups = computed(() => {
  const by = new Map<string, S['OsFindingView'][]>()
  for (const f of high.value) {
    const key = `${f.sourcePackage} ${f.candidate}`
    by.set(key, [...(by.get(key) ?? []), f])
  }
  return [...by.entries()].map(([key, findings]) => ({
    key,
    first: findings[0]!,
    since: findings.map((f) => f.firstSeenAt).sort()[0]!,
    findings,
  }))
})
const selected = ref<string[]>([])
// A finding that went (installed, or acknowledged meanwhile) leaves the selection.
watch(high, (findings) => {
  const open = new Set(findings.filter((f) => !f.acknowledgement).map((f) => f.id))
  selected.value = selected.value.filter((id) => open.has(id))
})
const chosen = computed(() => high.value.filter((f) => selected.value.includes(f.id)))
const acknowledging = ref(false)
function acknowledged(view: S['OsUpdatesView']) {
  selected.value = []
  emit('changed', view)
}

async function unacknowledge(f: S['OsFindingView']) {
  if (
    !(await ask(
      'Remove the acknowledgement?',
      `${f.cve} in ${f.sourcePackage} alerts again (466) at the next heartbeat while its update is pending.`,
      'Remove',
    ))
  )
    return
  const view = await run(() => api.linux.osUpdates.unacknowledge(props.view.sensorId, f.id), {
    success: 'Acknowledgement removed',
    invalidate: osUpdatesInvalidate(props.view.sensorId),
  })
  if (view) emit('changed', view)
}
const history = computed(() => ({ name: 'sensor', params: { id: props.view.sensorId, tab: 'history' } }))
</script>

<template>
  <section v-if="high.length" class="panel high-risk" aria-labelledby="high-risk-title">
    <div class="panel-header">
      <span id="high-risk-title">High risk</span>
      <Tag :value="String(high.length)" severity="danger" />
      <span class="spacer" />
      <RouterLink :to="history" class="small normal">History</RouterLink>
      <Button
        v-if="canRun"
        label="Acknowledge…"
        icon="pi pi-check-square"
        size="small"
        severity="secondary"
        :disabled="!chosen.length"
        :title="chosen.length ? undefined : 'Choose the findings to acknowledge first.'"
        @click="acknowledging = true"
      />
    </div>
    <div class="panel-body body">
      <p v-if="view.highRiskMessage" class="small muted message">{{ view.highRiskMessage }}</p>
      <article v-for="g in groups" :key="g.key" class="group">
        <div class="line">
          <strong>{{ g.first.sourcePackage }}</strong>
          <span class="mono small">{{ g.first.installed ?? '?' }} → {{ g.first.candidate }}</span>
          <span v-if="g.first.packages && g.first.packages !== g.first.sourcePackage" class="muted small ellipsis">{{
            g.first.packages
          }}</span>
          <span class="muted small">pending since <RelativeTime :value="g.since" /></span>
        </div>
        <ul class="findings">
          <li v-for="f in g.findings" :key="f.id" :class="{ acknowledged: !!f.acknowledgement }">
            <div class="line">
              <Checkbox
                v-if="canRun && !f.acknowledgement"
                v-model="selected"
                :value="f.id"
                :input-id="`finding-${f.id}`"
                :aria-label="`Choose ${f.cve} in ${f.sourcePackage}`"
              />
              <label :for="`finding-${f.id}`" class="mono cve">{{ f.cve }}</label>
              <Tag v-if="f.acknowledgement" value="acknowledged" severity="secondary" />
            </div>
            <FindingReasons :reasons="f.reasons" class="reasons" />
            <div v-if="f.acknowledgement" class="ack small">
              Acknowledged<template v-if="f.acknowledgement.byName"> by {{ f.acknowledgement.byName }}</template> until
              <time :datetime="f.acknowledgement.until">{{ formatAbsolute(f.acknowledgement.until) }}</time>:
              <span class="reason">{{ f.acknowledgement.reason }}</span>
              <Button
                v-if="canRun"
                label="Remove acknowledgement"
                size="small"
                link
                class="remove"
                @click="unacknowledge(f)"
              />
            </div>
            <div v-else-if="f.acknowledgementEndedAt" class="small muted">
              An acknowledgement ended <RelativeTime :value="f.acknowledgementEndedAt" /><template
                v-if="f.acknowledgementEndedReason"
                >: {{ f.acknowledgementEndedReason }}</template
              >
            </div>
          </li>
        </ul>
      </article>
    </div>
    <AcknowledgeFindingsDialog
      v-if="canRun"
      v-model:visible="acknowledging"
      :sensor-id="view.sensorId"
      :findings="chosen"
      :max-days="view.acknowledgeMaxDays"
      @done="acknowledged"
    />
  </section>
</template>

<style scoped>
.high-risk {
  border-color: var(--pnmt-fail-soft);
}

.spacer {
  flex: 1;
}

.normal {
  font-weight: 400;
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.message {
  margin: 0;
}

.group {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
  min-width: 0;
}

.findings {
  list-style: none;
  margin: 0;
  padding: 0 0 0 var(--pnmt-space-3);
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  border-left: 2px solid var(--pnmt-fail-soft);
}

.cve {
  font-weight: 600;
  color: var(--pnmt-fail);
}

.acknowledged .cve {
  color: var(--pnmt-muted);
}

.acknowledged .reasons {
  opacity: 0.6;
}

.reasons {
  margin-left: var(--pnmt-space-1);
}

.ack {
  color: var(--pnmt-muted);
}

.ack .reason {
  color: var(--pnmt-text);
}

.remove {
  padding: 0 var(--pnmt-space-1);
}

.small {
  font-size: 0.857rem;
}
</style>
