<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import { formatAbsolute } from '@/format'
import { formatBytes } from '@/format/bytes'
import RuleOutcome from './RuleOutcome.vue'
import VerdictLine from './VerdictLine.vue'

/**
 * *Try the rules* (docs/advanced-sensor-plan.md §6.6, §10.3): the saved rules against the latest report, a stored one, or
 * pasted JSON, with each rule's outcome (its values and why) and the verdict the sensor would have. Nothing is stored, no
 * event is written, nobody is notified. A stored report needs Monitoring: Details when the data names people (§8.3); pasted
 * JSON doesn't. The server reads the paste as it reads a report: what it can't read comes back as `error`.
 */
type Source = 'latest' | 'stored' | 'paste'

const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ sensor: S['SensorDetail'] }>()

const id = computed(() => props.sensor.id)
const a = computed(() => props.sensor.advanced ?? null)
const canSee = computed(() => a.value?.canSeeData ?? false)
const hasReport = computed(() => !!a.value?.latest)
const rules = computed(() => a.value?.rules ?? [])

const source = ref<Source>('latest')
const reportId = ref<string | null>(null)
const pasted = ref('')
const result = ref<S['AdvancedTestResult'] | null>(null)
const failure = ref<string | null>(null)
const busy = ref(false)

const sources = computed(() => [
  { label: 'The latest report', value: 'latest', disabled: !canSee.value || !hasReport.value },
  { label: 'A stored report', value: 'stored', disabled: !canSee.value || !hasReport.value },
  { label: 'Pasted JSON', value: 'paste', disabled: false },
])

const reports = useQuery({
  queryKey: computed(() => [...keys.advanced(id.value), 'reports', 'tester']),
  queryFn: () => api.advanced.reports(id.value, { pageSize: 100 }),
  enabled: computed(() => visible.value && source.value === 'stored' && canSee.value),
})
const reportOptions = computed(() =>
  (reports.data.value?.items ?? []).map((r) => ({
    value: r.id,
    label: [
      formatAbsolute(r.receivedAt),
      r.repeats > 0 ? `×${r.repeats + 1}` : null,
      !r.valid ? "couldn't be read" : r.code !== null ? String(r.code) : 'judging…',
      r.status,
    ]
      .filter(Boolean)
      .join(' · '),
  })),
)

const pastedBytes = computed(() => new TextEncoder().encode(pasted.value).length)
const ready = computed(() =>
  source.value === 'stored' ? !!reportId.value : source.value === 'paste' ? !!pasted.value.trim() : hasReport.value,
)

async function tryRules() {
  if (!ready.value || busy.value) return
  busy.value = true
  failure.value = null
  try {
    result.value = await api.advanced.test(
      id.value,
      source.value === 'stored'
        ? { reportId: reportId.value }
        : source.value === 'paste'
          ? { report: pasted.value }
          : {},
    )
  } catch (e) {
    result.value = null
    failure.value =
      e instanceof ApiError && e.status === 429
        ? `${e.message}`
        : `Not tried: ${e instanceof Error ? e.message : String(e)}`
  } finally {
    busy.value = false
  }
}

watch(
  visible,
  (open) => {
    if (!open) return
    result.value = null
    failure.value = null
    reportId.value = null
    source.value = canSee.value && hasReport.value ? 'latest' : 'paste'
    if (source.value === 'latest') void tryRules()
  },
  { immediate: true },
)
// A new source, report or paste: the answer shown was for the one before.
watch([source, reportId, pasted], () => {
  result.value = null
  failure.value = null
})

const verdictState = computed(() => {
  const state = result.value?.state
  return state === 'ok' ? 'OK' : state === 'warning' ? 'warning (the sensor stays OK)' : state
})
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Try the rules"
    modal
    :style="{ width: '52rem' }"
    :breakpoints="{ '860px': '96vw' }"
    class="rule-tester"
  >
    <div class="form">
      <p class="muted intro">
        The {{ rules.length }} saved rule{{ rules.length === 1 ? '' : 's' }} against a report. Nothing is stored, and
        nobody is notified.
      </p>
      <div class="field">
        <span id="tester-source" class="label">Report</span>
        <SelectButton
          v-model="source"
          :options="sources"
          option-label="label"
          option-value="value"
          option-disabled="disabled"
          :allow-empty="false"
          aria-labelledby="tester-source"
        />
        <small v-if="!canSee" class="field-hint"
          >The data of this sensor names people: its stored reports need Monitoring: Details. Pasted JSON works.</small
        >
        <small v-else-if="!hasReport" class="field-hint">No report yet: paste a sample to try the rules.</small>
      </div>

      <div v-if="source === 'stored'" class="field">
        <label for="tester-report">Stored report</label>
        <Select
          v-model="reportId"
          input-id="tester-report"
          :options="reportOptions"
          option-label="label"
          option-value="value"
          :loading="reports.isFetching.value"
          placeholder="Pick a report"
          empty-message="No reports"
          @update:model-value="tryRules"
        />
        <small v-if="reports.isError.value" class="field-error"
          >The reports can't be loaded: {{ (reports.error.value as Error | null)?.message }}</small
        >
      </div>

      <div v-else-if="source === 'paste'" class="field">
        <label for="tester-paste">JSON</label>
        <Textarea
          id="tester-paste"
          v-model="pasted"
          rows="10"
          class="mono paste"
          spellcheck="false"
          placeholder='{ "status": "ok", "disk": { "free_gb": 3.2 } }'
        />
        <small class="field-hint">{{ formatBytes(pastedBytes) }}, read as a report is read: at most 256 KiB.</small>
      </div>

      <div class="form-actions">
        <Button
          :label="result ? 'Try again' : 'Try'"
          icon="pi pi-play"
          size="small"
          :loading="busy"
          :disabled="!ready || busy"
          @click="tryRules"
        />
      </div>

      <Message v-if="failure" severity="error" :closable="false" size="small" role="alert">{{ failure }}</Message>

      <section v-if="result" class="results" aria-live="polite" aria-labelledby="tester-results">
        <h3 id="tester-results" class="heading">The sensor would be</h3>
        <Message v-if="result.error" severity="error" :closable="false" size="small">{{ result.error }}</Message>
        <div v-if="result.code !== null" class="verdict">
          <VerdictLine :code="result.code" :state="verdictState" :message="result.message" />
        </div>
        <ol v-if="result.results.length" class="outcomes" aria-label="Each rule">
          <li v-for="r in result.results" :key="r.ruleId">
            <div class="rule-head">
              <span class="name">{{ r.name }}</span>
              <span class="mono small muted condition">{{ r.condition }}</span>
              <span v-if="r.severity === 'Warn'" class="muted small">warns</span>
            </div>
            <RuleOutcome :result="r" />
          </li>
        </ol>
        <p v-else-if="!result.error" class="muted">No rules: every readable report is OK (295).</p>
      </section>
    </div>
    <template #footer>
      <Button label="Close" severity="secondary" text @click="visible = false" />
    </template>
  </Dialog>
</template>

<style scoped>
.intro {
  margin: 0;
}

.label {
  font-weight: 500;
}

.paste {
  font-family: var(--pnmt-mono);
  font-size: 0.9286rem;
  width: 100%;
}

.results {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  border-top: 1px solid var(--pnmt-border);
  padding-top: var(--pnmt-space-3);
}

.heading {
  font-size: 1rem;
  margin: 0;
}

.outcomes {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
}

.outcomes li {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: var(--pnmt-space-2) 0;
  border-bottom: 1px solid var(--pnmt-border);
  min-width: 0;
}

.outcomes li:last-child {
  border-bottom: 0;
}

.rule-head {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
}

.name {
  font-weight: 500;
}

.condition {
  overflow-wrap: anywhere;
}

.small {
  font-size: 0.857rem;
}

:deep(.p-selectbutton) {
  flex-wrap: wrap;
}
</style>
