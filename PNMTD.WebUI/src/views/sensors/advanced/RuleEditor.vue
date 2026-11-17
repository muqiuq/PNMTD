<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import AutoComplete from 'primevue/autocomplete'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import RadioButton from 'primevue/radiobutton'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import { useNotify } from '@/composables/useNotify'
import { formatDuration } from '@/format'
import { nameFromPath, operandHint, operatorGroups, operatorInfo, parseDuration, type Operator } from './advanced'
import RuleOutcome from './RuleOutcome.vue'
import {
  MAX_NAME,
  MAX_NOTE,
  QUANTIFIERS,
  SEVERITIES,
  WHEN_MISSING,
  blankDraft,
  draftErrors,
  draftOf,
  fieldErrors,
  findPath,
  kindLabel,
  operandError,
  operatorNote,
  outcomeLine,
  pathKind,
  ruleRequest,
  showsQuantifier,
  showsWhenMissing,
  suggestionFor,
  usesCase,
  type RuleDraft,
} from './rules'
import { useApplyAdvanced } from './useAdvanced'

/**
 * The rule editor (docs/advanced-sensor-plan.md §10.3), a dialog: a new rule (with `initial` from a value on the Data tab) or
 * `rule` to change. The path comes with the latest report's paths and their values (GET paths); the operators are grouped
 * as in §6.3, those that suit the path's value first, every one reachable; the operand fields follow the operator with
 * their hints, checked as the server will (§6.3). Below, live, what the latest report makes of the draft (§6.6: POST test,
 * debounced, only for a complete draft). Saving answers with the sensor's detail once re-judged (§8.3, up to 5 s): it goes
 * into the sensor's page here, and `saved` carries it.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{
  sensor: S['SensorDetail']
  rule?: S['AdvancedRuleItem'] | null
  initial?: Partial<S['AdvancedRuleRequest']> | null
}>()
const emit = defineEmits<{ saved: [detail: S['AdvancedSensorDetail']] }>()

/** The test endpoint allows 30 tries a minute (§8.3): the preview waits for a pause in the typing. */
const PREVIEW_DELAY = 600

const notify = useNotify()
const apply = useApplyAdvanced()
const id = computed(() => props.sensor.id)
const a = computed(() => props.sensor.advanced ?? null)
const canSee = computed(() => a.value?.canSeeData ?? false)

const draft = reactive<RuleDraft>(blankDraft())
// Whether the person chose the operator and the name: until then, picking a path suggests them.
const chosen = reactive({ operator: false, name: false })
const attempted = ref(false)
const saveErrors = ref<Record<string, string>>({})
const busy = ref(false)

// The live preview's state (§6.6), declared before the watchers that reset it.
const previewResult = ref<S['AdvancedRuleResult'] | null>(null)
const previewMessage = ref<string | null>(null)
const previewErrors = ref<Record<string, string>>({})
// A try waits for the pause in the typing, or for the server's answer.
const scheduled = ref(false)
const inFlight = ref(false)
const previewWaiting = computed(() => scheduled.value || inFlight.value)
let timer: ReturnType<typeof setTimeout> | undefined
let sequence = 0
let lastKey = ''
// Each opening tries the draft again, even when it is the same as last time.
const opened = ref(0)

function resetPreview() {
  stopPreview()
  previewResult.value = null
  previewMessage.value = null
  previewErrors.value = {}
  lastKey = ''
}
function stopPreview() {
  clearTimeout(timer)
  sequence++
  scheduled.value = false
  inFlight.value = false
}
onBeforeUnmount(stopPreview)

watch(
  visible,
  (open) => {
    if (!open) {
      stopPreview()
      return
    }
    Object.assign(draft, draftOf(props.rule ?? props.initial))
    chosen.operator = !!(props.rule || props.initial?.operator)
    chosen.name = !!(props.rule || props.initial?.name)
    attempted.value = false
    saveErrors.value = {}
    resetPreview()
    opened.value++
  },
  { immediate: true },
)
// A change after a refused save: the server's messages were for the old draft.
watch(draft, () => (saveErrors.value = {}))

// --- the path, with the latest report's paths ------------------------------------------------------------------------
const paths = useQuery({
  queryKey: computed(() => [...keys.advanced(id.value), 'paths']),
  queryFn: () => api.advanced.paths(id.value),
  enabled: computed(() => visible.value && canSee.value && !!a.value?.latest),
  staleTime: 30_000,
})
const pathList = computed(() => paths.data.value ?? [])
const suggestPaths = computed(() => canSee.value && !!a.value?.latest)
const suggestions = ref<string[]>([])
function complete(event: { query: string }) {
  const q = event.query.trim().toLowerCase()
  suggestions.value = pathList.value
    .filter((p) => !q || p.path.toLowerCase().includes(q))
    .slice(0, 100)
    .map((p) => p.path)
}
const previewOf = (path: string) => pathList.value.find((p) => p.path === path) ?? null
const pathItem = computed(() => findPath(pathList.value, draft.path))
const kind = computed(() => pathKind(pathItem.value?.kind))

function pathPicked(event: { value: unknown }) {
  const item = typeof event.value === 'string' ? previewOf(event.value) : null
  if (!item || props.rule) return
  if (!chosen.operator) {
    const suggested = suggestionFor(item)
    draft.operator = suggested.operator
    draft.operand = suggested.operand ?? ''
    draft.operand2 = ''
  }
}
watch(
  () => draft.path,
  (path) => {
    if (!chosen.name && !props.rule) draft.name = path.trim() ? nameFromPath(path.trim()) : ''
  },
)

// --- the operator and its operands ------------------------------------------------------------------------------------
const operatorOptions = computed(() =>
  operatorGroups(kind.value).map((g) => ({
    label: g.label,
    items: g.items.map((i) => ({ label: i.label, value: i.op, symbol: i.symbol, suits: i.suits })),
  })),
)
function chooseOperator(op: Operator) {
  draft.operator = op
  chosen.operator = true
}
const info = computed(() => operatorInfo(draft.operator))
const operandKind = computed(() => info.value.operand)
const hint = computed(() => operandHint(operandKind.value))
const note = computed(() => operatorNote(draft.operator))
const duration = computed(() => {
  if (operandKind.value !== 'duration') return null
  const seconds = parseDuration(draft.operand)
  return seconds ? formatDuration(seconds * 1000) : null
})
const quantifier = computed(() => showsQuantifier(draft))
const whenMissing = computed(() => showsWhenMissing(draft))
const caseMatters = computed(() => usesCase(draft.operator))

// --- the errors: the server's after a save, the editor's own, the server's for the preview -----------------------------
const local = computed(() => draftErrors(draft))
function errorOf(field: string): string | undefined {
  if (saveErrors.value[field]) return saveErrors.value[field]
  const mine = local.value[field]
  if (mine) {
    const typed = field === 'operand' ? draft.operand !== '' : field === 'operand2' ? draft.operand2 !== '' : false
    if (attempted.value || typed || field === 'note') return mine
  }
  return previewErrors.value[field]
}
const generalError = computed(() => saveErrors.value._ ?? null)

// --- live: the draft against the latest report (§6.6) ----------------------------------------------------------------
/** Why there is nothing to try the draft against, when there isn't. */
const previewBlocked = computed<string | null>(() => {
  const advanced = a.value
  if (!advanced) return null
  if (!advanced.canSeeData) {
    return 'The data of this sensor names people: trying a rule against its reports needs Monitoring: Details.'
  }
  if (!advanced.latest) return 'No report yet: the rule is judged when the first one arrives.'
  if (!advanced.latest.valid) {
    return `The latest report couldn't be read${advanced.latest.error ? `: ${advanced.latest.error}` : '.'}`
  }
  return null
})
/** The draft to try, once it is complete: a path, an operand that fits, a name (or the path's) and a note within bounds. */
const previewRequest = computed<S['AdvancedRuleRequest'] | null>(() => {
  if (previewBlocked.value || !draft.path.trim() || operandError(draft)) return null
  if (draft.name.trim().length > MAX_NAME || draft.note.length > MAX_NOTE) return null
  const request = ruleRequest(draft, props.rule?.id)
  request.name ||= nameFromPath(request.path ?? '') || 'Draft'
  return request
})
watch(
  () => (visible.value ? { request: previewRequest.value, opened: opened.value } : null),
  (next) => {
    const request = next?.request ?? null
    clearTimeout(timer)
    scheduled.value = false
    if (!request) {
      resetPreview()
      return
    }
    const key = JSON.stringify(request)
    if (key === lastKey) return
    scheduled.value = true
    timer = setTimeout(() => {
      scheduled.value = false
      void tryDraft(request, key)
    }, PREVIEW_DELAY)
  },
  { immediate: true },
)

async function tryDraft(request: S['AdvancedRuleRequest'], key: string) {
  const mine = ++sequence
  lastKey = key
  inFlight.value = true
  try {
    const result = await api.advanced.test(id.value, { rules: [request] })
    if (mine !== sequence) return
    previewErrors.value = {}
    previewResult.value = result.results[0] ?? null
    previewMessage.value = result.results.length ? null : result.error
  } catch (e) {
    if (mine !== sequence) return
    previewResult.value = null
    lastKey = ''
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      previewErrors.value = fieldErrors(e.errors, 'rules[0].')
      previewMessage.value = previewErrors.value._ ?? 'The server doesn’t take this rule yet: see the fields above.'
    } else if (e instanceof ApiError && e.status === 429) {
      previewErrors.value = {}
      previewMessage.value = 'Tried too often in a minute: the preview resumes with the next change in a moment.'
    } else {
      previewErrors.value = {}
      previewMessage.value = `Not tried: ${e instanceof Error ? e.message : String(e)}`
    }
  } finally {
    if (mine === sequence) inFlight.value = false
  }
}

// --- saving -----------------------------------------------------------------------------------------------------------
async function save() {
  attempted.value = true
  saveErrors.value = {}
  if (Object.keys(local.value).length || busy.value) return
  const advanced = a.value
  const before = new Set(advanced?.rules.map((r) => r.id) ?? [])
  const editing = props.rule ?? null
  busy.value = true
  try {
    const body = ruleRequest(draft)
    const detail = editing
      ? await api.advanced.rules.update(id.value, editing.id, body)
      : await api.advanced.rules.create(id.value, body)
    stopPreview()
    void apply(id.value, detail)
    emit('saved', detail)
    const saved = editing ? detail.rules.find((r) => r.id === editing.id) : detail.rules.find((r) => !before.has(r.id))
    notify.success(
      editing ? 'Rule saved' : 'Rule added',
      saved?.last && saved.enabled
        ? `With the last report: ${outcomeLine(saved.last, detail.canSeeData)}`
        : (saved?.condition ?? undefined),
    )
    visible.value = false
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      saveErrors.value = fieldErrors(e.errors)
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="rule ? 'Edit rule' : 'Add rule'"
    modal
    :style="{ width: '46rem' }"
    :breakpoints="{ '760px': '96vw' }"
    class="rule-editor"
  >
    <form class="form" novalidate @submit.prevent="save">
      <div class="field">
        <label for="rule-name">Name</label>
        <InputText
          id="rule-name"
          v-model="draft.name"
          :maxlength="MAX_NAME"
          placeholder="Disk space"
          :invalid="!!errorOf('name')"
          @input="chosen.name = true"
        />
        <small v-if="errorOf('name')" class="field-error" role="alert">{{ errorOf('name') }}</small>
        <small v-else class="field-hint">Names the rule in the sensor's message and its notifications.</small>
      </div>

      <div class="field">
        <label for="rule-path">Path</label>
        <AutoComplete
          v-if="suggestPaths"
          v-model="draft.path"
          input-id="rule-path"
          input-class="mono"
          :suggestions="suggestions"
          :delay="100"
          dropdown
          dropdown-mode="blank"
          fluid
          placeholder="disk.free_gb"
          :invalid="!!errorOf('path')"
          :pt="{ dropdown: { 'aria-label': 'Show the paths of the latest report' } }"
          @complete="complete"
          @option-select="pathPicked"
        >
          <template #option="{ option }">
            <span class="path-option">
              <span class="mono ellipsis">{{ option }}</span>
              <span class="muted small ellipsis">{{
                previewOf(option)?.preview ?? kindLabel(pathKind(previewOf(option)?.kind))
              }}</span>
            </span>
          </template>
        </AutoComplete>
        <InputText
          v-else
          id="rule-path"
          v-model="draft.path"
          class="mono"
          placeholder="disk.free_gb"
          :invalid="!!errorOf('path')"
        />
        <small v-if="errorOf('path')" class="field-error" role="alert">{{ errorOf('path') }}</small>
        <small v-else-if="pathItem" class="field-hint"
          >Now <span class="mono">{{ pathItem.preview ?? kindLabel(kind) }}</span><template v-if="pathItem.preview">
            ({{ kindLabel(kind) }})</template
          >, in the latest report.</small
        >
        <small v-else class="field-hint"
          >As <span class="mono">disk.free_gb</span>, <span class="mono">jobs[0].state</span>, or
          <span class="mono">jobs[*].state</span> for every element.</small
        >
      </div>

      <div class="field">
        <label for="rule-operator">Condition</label>
        <Select
          :model-value="draft.operator"
          input-id="rule-operator"
          :options="operatorOptions"
          option-label="label"
          option-value="value"
          option-group-label="label"
          option-group-children="items"
          filter
          filter-placeholder="Find an operator"
          scroll-height="22rem"
          :invalid="!!errorOf('operator')"
          @update:model-value="chooseOperator"
        >
          <template #option="{ option }">
            <span class="operator-option" :class="{ unusual: !option.suits }">
              <span>{{ option.label }}</span>
              <span class="muted mono small">{{ option.symbol }}</span>
              <span v-if="!option.suits" class="muted small">· not usual for {{ kindLabel(kind) }}</span>
            </span>
          </template>
        </Select>
        <small v-if="errorOf('operator')" class="field-error" role="alert">{{ errorOf('operator') }}</small>
        <small v-else-if="kind" class="field-hint"
          >The value is {{ kindLabel(kind) }}: the conditions for it come first in each group.</small
        >
        <small v-if="note" class="field-hint">{{ note }}</small>
      </div>

      <div v-if="operandKind === 'range'" class="form-grid">
        <div class="field">
          <label for="rule-operand">From</label>
          <InputText
            id="rule-operand"
            v-model="draft.operand"
            inputmode="decimal"
            class="mono"
            :invalid="!!errorOf('operand')"
          />
          <small v-if="errorOf('operand')" class="field-error" role="alert">{{ errorOf('operand') }}</small>
        </div>
        <div class="field">
          <label for="rule-operand2">To</label>
          <InputText
            id="rule-operand2"
            v-model="draft.operand2"
            inputmode="decimal"
            class="mono"
            :invalid="!!errorOf('operand2')"
          />
          <small v-if="errorOf('operand2')" class="field-error" role="alert">{{ errorOf('operand2') }}</small>
        </div>
        <small class="field-hint wide">{{ hint }} Both ends are included.</small>
      </div>
      <div v-else-if="operandKind" class="field">
        <label for="rule-operand">{{
          operandKind === 'list' ? 'Values' : operandKind === 'pattern' ? 'Pattern' : 'Value'
        }}</label>
        <Textarea
          v-if="operandKind === 'list'"
          id="rule-operand"
          v-model="draft.operand"
          rows="4"
          class="mono"
          :invalid="!!errorOf('operand')"
        />
        <InputText
          v-else
          id="rule-operand"
          v-model="draft.operand"
          class="mono"
          :inputmode="operandKind === 'number' ? 'decimal' : operandKind === 'count' ? 'numeric' : undefined"
          :placeholder="operandKind === 'duration' ? '26h' : undefined"
          :invalid="!!errorOf('operand')"
        />
        <small v-if="errorOf('operand')" class="field-error" role="alert">{{ errorOf('operand') }}</small>
        <small v-else class="field-hint"
          >{{ hint }}<template v-if="duration"> That is {{ duration }}.</template></small
        >
      </div>

      <label v-if="caseMatters" class="check">
        <Checkbox v-model="draft.ignoreCase" binary input-id="rule-ignore-case" />
        Ignore case
      </label>

      <fieldset v-if="quantifier" class="choices">
        <legend>For several matches</legend>
        <div v-for="q in QUANTIFIERS" :key="q.value" class="choice">
          <RadioButton v-model="draft.quantifier" :input-id="`rule-quantifier-${q.value}`" :value="q.value" />
          <label :for="`rule-quantifier-${q.value}`"
            ><strong>{{ q.label }}</strong> <span class="muted small">{{ q.hint }}</span></label
          >
        </div>
        <small v-if="errorOf('quantifier')" class="field-error" role="alert">{{ errorOf('quantifier') }}</small>
      </fieldset>

      <div class="form-grid">
        <fieldset v-if="whenMissing" class="choices">
          <legend>When missing</legend>
          <div v-for="w in WHEN_MISSING" :key="w.value" class="choice">
            <RadioButton v-model="draft.whenMissing" :input-id="`rule-missing-${w.value}`" :value="w.value" />
            <label :for="`rule-missing-${w.value}`"
              ><strong>{{ w.label }}</strong> <span class="muted small">{{ w.hint }}</span></label
            >
          </div>
          <small v-if="errorOf('whenMissing')" class="field-error" role="alert">{{ errorOf('whenMissing') }}</small>
        </fieldset>
        <fieldset class="choices">
          <legend>When it doesn't hold</legend>
          <div v-for="s in SEVERITIES" :key="s.value" class="choice">
            <RadioButton v-model="draft.severity" :input-id="`rule-severity-${s.value}`" :value="s.value" />
            <label :for="`rule-severity-${s.value}`"
              ><strong>{{ s.label }}</strong> <span class="muted small">{{ s.hint }}</span></label
            >
          </div>
          <small v-if="errorOf('severity')" class="field-error" role="alert">{{ errorOf('severity') }}</small>
        </fieldset>
      </div>

      <div class="field">
        <label for="rule-note">Note <span class="muted">(optional)</span></label>
        <Textarea
          id="rule-note"
          v-model="draft.note"
          rows="2"
          :maxlength="MAX_NOTE"
          placeholder="why the rule is there, what to do when it fails"
          :invalid="!!errorOf('note')"
        />
        <small v-if="errorOf('note')" class="field-error" role="alert">{{ errorOf('note') }}</small>
      </div>

      <label class="check">
        <Checkbox v-model="draft.enabled" binary input-id="rule-enabled" />
        On: judged with every report
      </label>

      <Message v-if="generalError" severity="error" :closable="false" size="small">{{ generalError }}</Message>

      <section class="preview" aria-live="polite" aria-labelledby="rule-preview-title">
        <span id="rule-preview-title" class="preview-title">With the last report</span>
        <span v-if="previewBlocked" class="muted">{{ previewBlocked }}</span>
        <span v-else-if="!previewRequest" class="muted"
          >Complete the rule to see what the last report makes of it.</span
        >
        <RuleOutcome v-else-if="previewResult" :result="previewResult" :class="{ stale: previewWaiting }" />
        <span v-else-if="previewMessage" class="muted">{{ previewMessage }}</span>
        <span v-else class="muted">Trying…</span>
        <i v-if="previewWaiting" class="pi pi-spin pi-spinner muted waiting" aria-label="Trying the rule" />
      </section>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        :label="rule ? 'Save rule' : 'Add rule'"
        icon="pi pi-check"
        :loading="busy"
        :disabled="busy"
        @click="save"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.choices {
  border: 0;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.choices legend {
  padding: 0;
  margin-bottom: var(--pnmt-space-1);
  font-weight: 500;
}

.choice {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-2);
}

.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.small {
  font-size: 0.857rem;
}

.path-option {
  display: flex;
  align-items: baseline;
  gap: var(--pnmt-space-3);
  min-width: 0;
  max-width: 40rem;
}

.path-option > :last-child {
  margin-left: auto;
  max-width: 50%;
}

.operator-option {
  display: flex;
  align-items: baseline;
  gap: var(--pnmt-space-2);
}

.operator-option.unusual > :first-child {
  color: var(--pnmt-muted);
}

.preview {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
}

.preview-title {
  font-weight: 600;
}

.preview-title::after {
  content: ':';
}

.preview .stale {
  opacity: 0.6;
}

.waiting {
  margin-left: auto;
}

.mono :deep(input),
:deep(input.mono) {
  font-family: var(--pnmt-mono);
}
</style>
