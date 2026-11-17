<script setup lang="ts">
import Message from 'primevue/message'
import { computed } from 'vue'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNow } from '@/composables/useNow'
import { formatAbsolute } from '@/format'
import { formatBytes } from '@/format/bytes'
import { codeTone, healthTone } from './advanced'
import { codeLabel, HEAD_LENGTH, headOf, repeatText, skewText } from './dataTab'

/**
 * A report's headline (docs/advanced-sensor-plan.md §10.3): when it was received (*×1 440 until 10:02* for repeats), when
 * the app measured it with a hint when its clock is off, where it came from and its size, the verdict at this report
 * (*judging…* until the evaluator has judged it), and the app's `status` and `message`. A report that couldn't be read
 * shows its reason and the first 2 KB of what arrived, as text.
 */
const props = defineProps<{
  report: S['AdvancedReportItem']
  body: string | null
  evaluationError?: string | null
}>()
const now = useNow()
const repeats = computed(() => repeatText(props.report, now.value))
const skew = computed(() => skewText(props.report.measuredAt, props.report.receivedAt))
const statusTone = computed(() => healthTone(props.report.status) ?? 'muted')
const head = computed(() => (props.report.valid ? null : headOf(props.body)))
</script>

<template>
  <section class="panel headline" aria-label="The report's headline">
    <div class="panel-body">
      <dl class="stats">
        <div>
          <dt>Received</dt>
          <dd>
            <RelativeTime :value="report.receivedAt" />
            <span class="muted small"> · {{ formatAbsolute(report.receivedAt) }}</span>
            <div
              v-if="repeats"
              class="small"
              :title="`The same report ${report.repeats} more time${report.repeats === 1 ? '' : 's'}, the last at ${formatAbsolute(report.lastReceivedAt)}`"
            >
              {{ repeats }}
            </div>
          </dd>
        </div>
        <div v-if="report.measuredAt">
          <dt>Measured</dt>
          <dd>
            {{ formatAbsolute(report.measuredAt) }}
            <div v-if="skew" class="small tone-warn"><i class="pi pi-clock" aria-hidden="true" /> {{ skew }}</div>
          </dd>
        </div>
        <div v-if="report.source">
          <dt>From</dt>
          <dd class="mono">{{ report.source }}</dd>
        </div>
        <div>
          <dt>Size</dt>
          <dd>{{ formatBytes(report.bytes) }}</dd>
        </div>
        <div>
          <dt>Verdict</dt>
          <dd>
            <span v-if="!report.evaluated" class="judging"
              ><i class="pi pi-spin pi-spinner" aria-hidden="true" /> judging…</span
            >
            <template v-else>
              <span class="code" :class="`tone-${codeTone(report.code)}`">{{ report.code ?? '–' }}</span>
              {{ codeLabel(report.code) }}
              <span v-if="report.failed || report.warnings" class="muted small">
                <template v-if="report.failed">
                  · {{ report.failed }} fail{{ report.failed === 1 ? 's' : '' }}</template
                >
                <template v-if="report.warnings">
                  · {{ report.warnings }} warn{{ report.warnings === 1 ? 's' : '' }}</template
                >
              </span>
            </template>
          </dd>
        </div>
      </dl>
      <p v-if="report.status || report.message" class="says">
        <span v-if="report.status" class="status" :class="`tone-${statusTone}`">{{ report.status }}</span>
        <span v-if="report.message" class="message">{{ report.message }}</span>
      </p>
      <Message v-if="!report.valid" severity="error" :closable="false">
        <div>{{ report.error ?? "The report couldn't be read." }}</div>
        <template v-if="head">
          <div class="small arrived">
            What arrived{{ (body?.length ?? 0) > HEAD_LENGTH ? ' (the first 2 KB)' : '' }}:
          </div>
          <pre class="head">{{ head }}</pre>
        </template>
        <div v-else-if="report.errorKind === 'TooLarge'" class="small">Too large to keep: none of it is stored.</div>
      </Message>
      <Message v-if="evaluationError" severity="warn" :closable="false" size="small">
        PNMT couldn't judge this report: {{ evaluationError }}
      </Message>
    </div>
  </section>
</template>

<style scoped>
.headline .panel-body {
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

.says {
  margin: 0;
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.status {
  font-weight: 600;
  font-size: 0.857rem;
  padding: 0.05rem 0.5rem;
  border-radius: 999px;
  background: var(--pnmt-none-soft);
}

.status.tone-ok {
  background: var(--pnmt-ok-soft);
}

.status.tone-warn {
  background: var(--pnmt-warn-soft);
}

.status.tone-fail {
  background: var(--pnmt-fail-soft);
}

.message {
  min-width: 0;
  overflow-wrap: anywhere;
}

.code {
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  font-weight: 600;
  padding: 0.05rem 0.35rem;
  border-radius: var(--p-border-radius-sm);
  background: var(--pnmt-none-soft);
}

.code.tone-ok {
  background: var(--pnmt-ok-soft);
}

.code.tone-warn {
  background: var(--pnmt-warn-soft);
}

.code.tone-fail {
  background: var(--pnmt-fail-soft);
}

.judging {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.arrived {
  margin-top: var(--pnmt-space-2);
}

.head {
  margin: var(--pnmt-space-1) 0 0;
  max-height: 16rem;
  overflow: auto;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
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

.tone-muted {
  color: var(--pnmt-muted);
}
</style>
