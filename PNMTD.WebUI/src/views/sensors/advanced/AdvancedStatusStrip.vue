<script setup lang="ts">
import Message from 'primevue/message'
import { computed } from 'vue'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { formatDuration } from '@/format'
import { codeTone, expectLabel, healthTone } from './advanced'

/**
 * An Advanced sensor at a glance (docs/advanced-sensor-plan.md §10.3): the last report, from where and how often one is
 * expected, or since when it is missing; the app's status and message; the rules in one line; *judging…* while reports
 * wait for the evaluator; and the reason when the latest report couldn't be read.
 */
const props = defineProps<{ sensor: S['SensorDetail'] }>()
const a = computed(() => props.sensor.advanced!)
const rules = computed(() => a.value.rules.filter((r) => r.enabled))
const counts = computed(() => ({
  hold: rules.value.filter((r) => r.last?.outcome === 'Pass').length,
  fail: rules.value.filter((r) => r.last?.outcome === 'Fail').length,
  warn: rules.value.filter((r) => r.last?.outcome === 'Warn').length,
}))
const statusTone = computed(() => healthTone(a.value.latest?.status) ?? 'muted')
const skew = computed(() => {
  const s = a.value.latest?.clockSkewSeconds
  if (s === null || s === undefined || Math.abs(s) <= 300) return null
  return `the app's clock is ${formatDuration(Math.abs(s) * 1000)} ${s < 0 ? 'behind' : 'ahead'}`
})
</script>

<template>
  <section class="panel strip" aria-label="Report status">
    <dl class="stats">
      <div>
        <dt>Last report</dt>
        <dd>
          <RelativeTime :value="a.lastReportAt" empty="none yet" />
          <span v-if="a.lastSource" class="muted small"> from {{ a.lastSource }}</span>
          <div class="muted small">
            <template v-if="a.stale && a.staleAt">
              <span class="tone-fail">missing since <RelativeTime :value="a.staleAt" /></span> ·
            </template>
            expected {{ expectLabel(sensor.interval)
            }}<template v-if="sensor.interval && sensor.gracePeriod">
              + {{ formatDuration(sensor.gracePeriod * 1000) }} grace</template
            >
          </div>
        </dd>
      </div>
      <div v-if="a.latest?.status || a.latest?.message">
        <dt>The app says</dt>
        <dd>
          <span v-if="a.latest?.status" class="status-word" :class="`tone-${statusTone}`">{{ a.latest.status }}</span>
          <span v-if="a.latest?.message" class="message" :title="a.latest.message">{{ a.latest.message }}</span>
          <div v-if="skew" class="muted small">{{ skew }}</div>
        </dd>
      </div>
      <div>
        <dt>Rules</dt>
        <dd>
          <template v-if="rules.length">
            {{ rules.length }} rule{{ rules.length === 1 ? '' : 's'
            }}<template v-if="counts.hold">
              · <span class="tone-ok">{{ counts.hold }} hold</span></template
            ><template v-if="counts.warn">
              · <span class="tone-warn">{{ counts.warn }} warn{{ counts.warn === 1 ? 's' : '' }}</span></template
            ><template v-if="counts.fail">
              · <span class="tone-fail">{{ counts.fail }} fail{{ counts.fail === 1 ? 's' : '' }}</span></template
            >
          </template>
          <span v-else class="muted">none: every report is OK</span>
          <div v-if="a.verdict" class="muted small">
            verdict <span :class="`tone-${codeTone(a.verdict.code)}`">{{ a.verdict.code }}</span>
            <template v-if="a.verdict.evaluatedAt"> · judged <RelativeTime :value="a.verdict.evaluatedAt" /></template>
          </div>
        </dd>
      </div>
      <div v-if="a.pending > 0">
        <dt>Evaluator</dt>
        <dd>
          <span class="judging"
            ><i class="pi pi-spin pi-spinner" aria-hidden="true" /> judging {{ a.pending }} report{{
              a.pending === 1 ? '' : 's'
            }}…</span
          >
        </dd>
      </div>
    </dl>
    <Message v-if="a.latest && !a.latest.valid" severity="error" :closable="false" size="small">
      {{ a.latest.error ?? "The latest report couldn't be read." }}
    </Message>
    <Message v-else-if="!a.canSeeData" severity="secondary" :closable="false" size="small">
      The data of this sensor names people: its values need Monitoring: Details.
    </Message>
  </section>
</template>

<style scoped>
.strip {
  padding: var(--pnmt-space-2) var(--pnmt-space-4);
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.stats {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2) var(--pnmt-space-6);
  margin: 0;
  align-items: flex-start;
}

.stats dt {
  font-size: 0.786rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pnmt-muted);
  font-weight: 600;
}

.stats dd {
  margin: 0;
  font-weight: 500;
}

.small {
  font-size: 0.857rem;
  font-weight: 400;
}

.message {
  display: inline-block;
  margin-left: var(--pnmt-space-1);
  max-width: 32rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  vertical-align: bottom;
  font-weight: 400;
}

.status-word {
  font-weight: 600;
}

.judging {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.tone-ok {
  color: var(--pnmt-ok);
}

.tone-warn {
  color: var(--pnmt-warn);
}

.tone-fail {
  color: var(--pnmt-fail);
}

@media (max-width: 599px) {
  .strip {
    padding: var(--pnmt-space-2) var(--pnmt-space-3);
  }
}
</style>
