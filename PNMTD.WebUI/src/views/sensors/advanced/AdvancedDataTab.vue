<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Textarea from 'primevue/textarea'
import { computed, nextTick, ref } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import JsonTree from '@/components/JsonTree.vue'
import JsonHighlight from '@/components/jsonTree/JsonHighlight.vue'
import { parseJson, prettyJson, type JsonPick } from '@/components/jsonTree/jsonTree'
import RelativeTime from '@/components/RelativeTime.vue'
import SensorUrlPanel from '@/components/SensorUrlPanel.vue'
import { useCopy } from '@/composables/useCopy'
import { useNotify } from '@/composables/useNotify'
import { formatAbsolute } from '@/format'
import { codeTone } from './advanced'
import { codeLabel, fieldDraft, marksFrom, outcomeRows, reportFileName, ruleDraft, ruleRows } from './dataTab'
import FieldCards from './FieldCards.vue'
import FieldEditor from './FieldEditor.vue'
import ReportHeadline from './ReportHeadline.vue'
import ReportHistory from './ReportHistory.vue'
import RuleEditor from './RuleEditor.vue'
import RuleOutcomes from './RuleOutcomes.vue'
import { useApplyAdvanced } from './useAdvanced'

/**
 * An Advanced sensor's *Data* tab (docs/advanced-sensor-plan.md §10.3): the latest report's headline, the fields with their
 * charts, the report itself as a tree (with the rules' marks, *Changed since the previous* and *Raw*), and the history,
 * whose rows open their report here. Values in the tree lead to *Add rule…* and *Add field…*. Before the first report:
 * the report URL and *Paste a sample*, to set up rules and fields first. When the data names people and the viewer lacks
 * Monitoring: Details, only the verdict and the rules' outcomes, and no call for the data (§13).
 */
const props = defineProps<{ sensor: S['SensorDetail']; canWrite: boolean }>()
const notify = useNotify()
const copy = useCopy()
const applyAdvanced = useApplyAdvanced()

const id = props.sensor.id
const a = computed(() => props.sensor.advanced!)
const canSee = computed(() => a.value.canSeeData)
const hasReport = computed(() => !!a.value.latest)

// --- the report shown: the latest, or one picked in the history -----------------------------------------------------------
/** The latest report is fetched again when the sensor's page says it changed: a new one, a repeat, or judged. */
const latestStamp = computed(() => {
  const l = a.value.latest
  const v = a.value.verdict
  return l ? [l.id, l.lastReceivedAt, l.evaluated, l.code, v?.failed ?? 0, v?.warnings ?? 0] : null
})
const latest = useQuery({
  queryKey: computed(() => [...keys.advanced(id), 'report', 'latest', latestStamp.value]),
  queryFn: () => api.advanced.latest(id),
  enabled: computed(() => canSee.value && hasReport.value),
  placeholderData: keepPreviousData,
})
const pickedId = ref<string | null>(null)
const picked = useQuery({
  queryKey: computed(() => [...keys.advanced(id), 'report', pickedId.value]),
  queryFn: () => api.advanced.report(id, pickedId.value!),
  enabled: computed(() => canSee.value && pickedId.value !== null),
})
const current = computed(() => (pickedId.value ? picked : latest))
const shown = computed(() => current.value.data.value ?? null)
const isLatest = computed(() => !pickedId.value)

interface Parsed {
  value?: unknown
  error?: string
}
const parse = (body: string | null | undefined): Parsed => {
  if (body === null || body === undefined) return { error: 'No text was kept of this report.' }
  try {
    return { value: parseJson(body) }
  } catch (e) {
    return { error: `Not valid JSON: ${(e as Error).message}` }
  }
}
const parsed = computed<Parsed>(() => (shown.value?.report.valid ? parse(shown.value.body) : {}))
const marks = computed(() => marksFrom(shown.value?.results))

function openReport(reportId: string) {
  pickedId.value = reportId === a.value.latest?.id ? null : reportId
  void nextTick(() => document.getElementById('advanced-report-title')?.scrollIntoView?.({ block: 'start' }))
}
function onDeleted(reportId: string | null) {
  if (reportId === null || reportId === pickedId.value) pickedId.value = null
}

// --- changed since the previous report ----------------------------------------------------------------------------------
const comparing = ref(false)
const previousId = computed(() => shown.value?.previousId ?? null)
const previous = useQuery({
  queryKey: computed(() => [...keys.advanced(id), 'report', previousId.value]),
  queryFn: () => api.advanced.report(id, previousId.value!),
  enabled: computed(() => comparing.value && canSee.value && previousId.value !== null),
})
const previousValue = computed(() => {
  const p = previous.data.value
  if (!comparing.value || !p || !p.report.valid) return undefined
  return parse(p.body).value
})
const previousNote = computed(() => {
  if (!comparing.value) return null
  if (!previousId.value) return 'There is no earlier report to compare with.'
  const p = previous.data.value
  if (previous.isError.value) return "The previous report couldn't be loaded."
  if (!p) return null
  if (!p.report.valid) return "The previous report couldn't be read, so nothing is compared."
  return `Compared with the report of ${formatAbsolute(p.report.receivedAt)}.`
})

// --- raw ------------------------------------------------------------------------------------------------------------------
const raw = ref(false)
const pretty = computed(() => (raw.value && shown.value?.body ? prettyJson(shown.value.body) : ''))
function download() {
  const s = shown.value
  if (!s?.body) return
  const url = URL.createObjectURL(new Blob([s.body], { type: 'application/json' }))
  const link = document.createElement('a')
  link.href = url
  link.download = reportFileName(props.sensor.name, s.report.receivedAt)
  link.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

// --- Add rule… and Add field… ---------------------------------------------------------------------------------------------
const ruleOpen = ref(false)
const ruleInitial = ref<Partial<S['AdvancedRuleRequest']> | null>(null)
const fieldOpen = ref(false)
const fieldInitial = ref<Partial<S['AdvancedFieldRequest']> | null>(null)
function addRule(pick: JsonPick) {
  ruleInitial.value = ruleDraft(pick)
  ruleOpen.value = true
}
function addField(pick: JsonPick) {
  fieldInitial.value = fieldDraft(pick, a.value.fields.filter((f) => f.chart).length)
  fieldOpen.value = true
}
async function saved(detail: S['AdvancedSensorDetail']) {
  await applyAdvanced(id, detail)
  if (tried.value) await trySample(tried.value.text)
}

// --- before the first report: paste a sample (§6.6) -----------------------------------------------------------------------
const sampleText = ref('')
const sampleError = ref<string | null>(null)
const trying = ref(false)
const tried = ref<{ text: string; result: S['AdvancedTestResult']; parsed: Parsed } | null>(null)
async function trySample(text = sampleText.value) {
  if (!text.trim()) return
  trying.value = true
  sampleError.value = null
  try {
    const result = await api.advanced.test(id, { report: text })
    tried.value = { text, result, parsed: parse(text) }
  } catch (e) {
    const message = e instanceof ApiError ? (e.field('report') ?? (e.status === 400 ? e.message : null)) : null
    if (message) sampleError.value = message
    else notify.error(e, 'Not tried')
  } finally {
    trying.value = false
  }
}
const sampleMarks = computed(() => marksFrom(tried.value?.result.results))
</script>

<template>
  <div class="data-tab">
    <!-- Without Monitoring: Details on a sensor whose data names people: the verdict and the outcomes, no values. -->
    <template v-if="!canSee">
      <Message severity="secondary" :closable="false">
        <i class="pi pi-lock" aria-hidden="true" /> The data of this sensor names people: Monitoring: Details.
      </Message>
      <section class="panel" aria-labelledby="advanced-verdict-title">
        <div id="advanced-verdict-title" class="panel-header">Verdict</div>
        <div class="panel-body column">
          <p v-if="a.verdict" class="verdict">
            <span class="code" :class="`tone-${codeTone(a.verdict.code)}`">{{ a.verdict.code }}</span>
            {{ codeLabel(a.verdict.code) }}
            <span v-if="a.verdict.evaluatedAt" class="muted small">
              · judged <RelativeTime :value="a.verdict.evaluatedAt"
            /></span>
          </p>
          <p v-else class="muted verdict">No report judged yet.</p>
          <RuleOutcomes v-if="a.rules.length" :rows="ruleRows(a.rules)" />
          <p v-else class="muted verdict">No rules: every report is OK.</p>
        </div>
      </section>
    </template>

    <!-- No report yet: where to send one, and a sample to set up the rules and fields first. -->
    <template v-else-if="!hasReport">
      <section class="panel">
        <EmptyState
          icon="pi pi-inbox"
          title="No report yet."
          :text="
            canWrite
              ? 'The app POSTs its JSON to the report URL below. Until then, paste a sample to set up rules and fields.'
              : 'The app hasn\'t sent a report yet.'
          "
        />
      </section>
      <section v-if="canWrite && sensor.integration" class="panel" aria-labelledby="advanced-send-title">
        <div id="advanced-send-title" class="panel-header">Send a report</div>
        <div class="panel-body">
          <SensorUrlPanel :integration="sensor.integration" :type="sensor.type" />
        </div>
      </section>
      <FieldCards v-if="a.fields.length || canWrite" :sensor="sensor" :can-write="canWrite" />
      <section class="panel" aria-labelledby="advanced-sample-title">
        <div id="advanced-sample-title" class="panel-header">Paste a sample</div>
        <div class="panel-body column">
          <p class="muted intro">
            A report as the app will send it: the rules judge it, and nothing is stored.<template v-if="canWrite">
              Pick values in it to add rules and fields before the app sends anything.</template
            >
          </p>
          <form class="form" novalidate @submit.prevent="trySample()">
            <div class="field">
              <label for="advanced-sample">The JSON</label>
              <Textarea
                id="advanced-sample"
                v-model="sampleText"
                rows="8"
                class="mono"
                spellcheck="false"
                placeholder='{ "status": "ok", "disk": { "free_gb": 412.5 } }'
                :invalid="!!sampleError"
              />
              <small v-if="sampleError" class="field-error">{{ sampleError }}</small>
            </div>
            <div class="form-actions">
              <Button type="submit" label="Try" icon="pi pi-play" :loading="trying" :disabled="!sampleText.trim()" />
            </div>
          </form>
          <template v-if="tried">
            <p class="verdict" aria-live="polite">
              <span class="code" :class="`tone-${codeTone(tried.result.code)}`">{{ tried.result.code ?? '–' }}</span>
              {{ tried.result.message ?? codeLabel(tried.result.code) }}
            </p>
            <Message v-if="tried.result.error" severity="error" :closable="false" size="small">
              {{ tried.result.error }}
            </Message>
            <RuleOutcomes v-if="tried.result.results.length" :rows="outcomeRows(tried.result.results, true)" />
            <JsonTree
              v-if="tried.parsed.value !== undefined"
              :value="tried.parsed.value"
              :marks="sampleMarks"
              :can-write="canWrite"
              label="The sample"
              @add-rule="addRule"
              @add-field="addField"
            />
          </template>
        </div>
      </section>
    </template>

    <!-- The reports. -->
    <template v-else>
      <ErrorState
        v-if="current.isError.value && !shown"
        :error="current.error.value"
        what="the report"
        @retry="current.refetch()"
      />
      <div v-else-if="!shown" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
      <template v-else>
        <Message v-if="!isLatest" severity="info" :closable="false" class="picked">
          <span>An earlier report, of {{ formatAbsolute(shown.report.receivedAt) }}, with its outcomes then.</span>
          <Button label="Back to the latest" icon="pi pi-arrow-up" size="small" text @click="pickedId = null" />
        </Message>
        <ReportHeadline :report="shown.report" :body="shown.body" :evaluation-error="shown.evaluationError" />
        <FieldCards :sensor="sensor" :can-write="canWrite" />
        <section v-if="shown.report.valid" class="panel" aria-labelledby="advanced-report-title">
          <div class="panel-header report-header">
            <span id="advanced-report-title">The report</span>
            <span class="spacer" />
            <Button
              v-if="!raw"
              label="Changed since the previous"
              icon="pi pi-history"
              size="small"
              :text="!comparing"
              :outlined="comparing"
              :aria-pressed="comparing"
              :disabled="!previousId"
              @click="comparing = !comparing"
            />
            <Button
              label="Raw"
              icon="pi pi-code"
              size="small"
              :text="!raw"
              :outlined="raw"
              :aria-pressed="raw"
              @click="raw = !raw"
            />
            <template v-if="raw && shown.body">
              <Button
                v-tooltip.top="'The report as received'"
                label="Copy"
                icon="pi pi-copy"
                size="small"
                text
                @click="copy(shown.body, 'The report')"
              />
              <Button
                v-tooltip.top="'The report as received'"
                label="Download"
                icon="pi pi-download"
                size="small"
                text
                @click="download"
              />
            </template>
          </div>
          <div class="panel-body column">
            <JsonHighlight v-if="raw && pretty" :text="pretty" />
            <template v-else-if="parsed.value !== undefined">
              <Message v-if="previousNote" severity="secondary" :closable="false" size="small">{{
                previousNote
              }}</Message>
              <JsonTree
                :value="parsed.value"
                :marks="marks"
                :previous="previousValue"
                label="The report's values"
                :can-write="canWrite"
                @add-rule="addRule"
                @add-field="addField"
              />
            </template>
            <Message v-else-if="parsed.error" severity="error" :closable="false">{{ parsed.error }}</Message>
          </div>
        </section>
      </template>
      <ReportHistory
        :sensor="sensor"
        :can-write="canWrite"
        :shown-id="shown?.report.id ?? null"
        @open="openReport"
        @deleted="onDeleted"
      />
    </template>

    <RuleEditor
      v-if="canWrite"
      v-model:visible="ruleOpen"
      :sensor="sensor"
      :rule="null"
      :initial="ruleInitial"
      @saved="saved"
    />
    <FieldEditor
      v-if="canWrite"
      v-model:visible="fieldOpen"
      :sensor="sensor"
      :field="null"
      :initial="fieldInitial"
      @saved="saved"
    />
  </div>
</template>

<style scoped>
.data-tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  min-width: 0;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.column {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  min-width: 0;
}

.intro,
.verdict {
  margin: 0;
}

.verdict {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: var(--pnmt-space-2);
  overflow-wrap: anywhere;
}

.small {
  font-size: 0.857rem;
}

.spacer {
  flex: 1;
}

.report-header {
  flex-wrap: wrap;
  padding-top: var(--pnmt-space-1);
  padding-bottom: var(--pnmt-space-1);
}

.picked :deep(.p-message-text) {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
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
  color: var(--pnmt-ok);
  background: var(--pnmt-ok-soft);
}

.code.tone-warn {
  color: var(--pnmt-warn);
  background: var(--pnmt-warn-soft);
}

.code.tone-fail {
  color: var(--pnmt-fail);
  background: var(--pnmt-fail-soft);
}
</style>
