<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { api, connectorRequest, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import ScriptArgumentInput from '../admin/servers/ScriptArgumentInput.vue'

/**
 * A connector's arguments (docs/connectors-plan.md §5, §13.5): per parameter, the caller passes it (*Exposed*, with its pattern, which
 * must match the whole value), PNMT sets it (*Fixed*), or neither does (*Omitted*: the script's default). A secure parameter is never
 * fixed. *Check a call…* tries sample arguments against all of it, the review too.
 */
const props = defineProps<{ connector: S['ConnectorDetail'] }>()
const session = useSession()
const canManage = computed(() => session.can('Connectors', 'Manage'))

interface Row {
  name: string
  mode: S['ConnectorArgumentMode']
  required: boolean
  pattern: string
  fixedValue: unknown
  description: string
  multiline: boolean
  parameter: S['ScriptParameter']
  kind: S['ConnectorValueKind']
  misfit: string | null
  tests: string
}
const rows = ref<Row[]>([])
function load(c: S['ConnectorDetail']) {
  rows.value = c.arguments.map((a) => ({
    name: a.name,
    mode: a.mode,
    required: a.required,
    pattern: a.pattern ?? '',
    fixedValue: a.fixedValue ?? null,
    description: a.description ?? '',
    multiline: a.multiline,
    parameter: a.parameter,
    kind: a.kind,
    misfit: a.misfit,
    tests: '',
  }))
}
load(props.connector)
watch(() => props.connector, load)
const modes = (r: Row) => [
  { label: 'Exposed', value: 'Exposed' },
  { label: 'Fixed', value: 'Fixed', disabled: r.parameter.isSecure },
  { label: 'Omitted', value: 'Omitted', disabled: r.parameter.mandatory },
]
const pending = computed(
  () =>
    props.connector.target.parseState === 'Pending' && props.connector.type === 'WindowsScript' && !rows.value.length,
)

// The pattern's tester: values typed by the admin, matched as a call would match them.
const results = ref<Record<string, S['ConnectorPatternResult'] | null>>({})
let timer: ReturnType<typeof setTimeout> | undefined
watch(
  () => rows.value.map((r) => [r.name, r.pattern, r.tests]),
  () => {
    clearTimeout(timer)
    timer = setTimeout(async () => {
      for (const r of rows.value) {
        if (!r.pattern.trim()) {
          results.value[r.name] = null
          continue
        }
        const values = r.tests.split('\n').filter((v) => v.length)
        results.value[r.name] = await api.connectors.testPattern({ pattern: r.pattern, values }).catch(() => null)
      }
    }, 400)
  },
  { deep: true },
)

const { run, busy } = useActions()
async function save() {
  await run(
    () =>
      api.connectors.update(
        props.connector.id,
        connectorRequest(props.connector, {
          arguments: rows.value.map((r) => ({
            name: r.name,
            mode: r.mode,
            required: r.required,
            pattern: r.mode === 'Exposed' && r.pattern.trim() ? r.pattern.trim() : null,
            fixedValue: (r.mode === 'Fixed'
              ? (r.fixedValue ?? null)
              : null) as S['ConnectorArgumentRequest']['fixedValue'],
            description: r.description.trim() || null,
            multiline: r.multiline,
          })),
        }),
      ),
    { success: 'Saved', invalidate: [keys.connectors] },
  )
}

// Check a call…: sample arguments and a note against checks 9 to 12, the review optional.
const checking = ref(false)
const samples = ref<Record<string, unknown>>({})
const note = ref('')
const withReview = ref(false)
const checked = ref<S['ConnectorCheckResult'] | null>(null)
const exposed = computed(() => rows.value.filter((r) => r.mode === 'Exposed'))
async function check() {
  const body: Record<string, unknown> = {}
  for (const r of exposed.value) {
    const v = samples.value[r.name]
    if (v !== undefined && v !== null && v !== '') body[r.name] = v
  }
  checked.value =
    (await run(() =>
      api.connectors.check(props.connector.id, {
        arguments: body as S['ConnectorCheckRequest']['arguments'],
        note: note.value || null,
        review: withReview.value,
      }),
    )) ?? null
}
</script>

<template>
  <section class="panel panel-body">
    <Message v-if="connector.target.needsReview" severity="warn" :closable="false" size="small">
      The text changed, and a choice below no longer fits it: calls are refused until the arguments are saved again.
    </Message>
    <Message v-if="pending" severity="info" :closable="false" size="small">
      The parameters are pending: a connected Windows agent parses the text, then they show here.
    </Message>
    <p v-else-if="!rows.length" class="muted">
      The {{ connector.target.function ? 'function' : 'script' }} has no parameters.
    </p>
    <div v-for="r in rows" :key="r.name" class="argument">
      <div class="head">
        <strong class="mono">{{ r.name }}</strong>
        <span class="muted small"
          >[{{ r.parameter.type }}]{{ r.parameter.mandatory ? ' · mandatory' : ''
          }}{{ r.parameter.isSecure ? ' · secret' : '' }}</span
        >
        <SelectButton
          v-model="r.mode"
          :options="modes(r)"
          option-label="label"
          option-value="value"
          option-disabled="disabled"
          :allow-empty="false"
          :disabled="!canManage"
          size="small"
          :aria-label="`${r.name}: exposed, fixed or omitted`"
        />
      </div>
      <p v-if="r.parameter.helpMessage" class="small muted">{{ r.parameter.helpMessage }}</p>
      <Message v-if="r.misfit" severity="warn" :closable="false" size="small">{{ r.misfit }}</Message>
      <small v-if="r.parameter.isSecure" class="field-hint"
        >Secret: exposed or omitted, never fixed. PNMT stores no script secrets.</small
      >
      <div v-if="r.mode === 'Exposed'" class="form-grid">
        <div class="field">
          <label :for="`pattern-${r.name}`">Pattern</label>
          <InputText
            :id="`pattern-${r.name}`"
            v-model="r.pattern"
            class="mono"
            :disabled="!canManage || r.kind === 'Bool'"
            :placeholder="r.kind === 'Bool' ? 'true or false: no pattern' : 'shop-[a-z0-9]{2,20}'"
          />
          <small class="field-hint"
            >The whole value must match. A value that doesn't is refused, and the caller hears the pattern.</small
          >
          <small v-if="results[r.name]?.error" class="error">{{ results[r.name]!.error }}</small>
          <small v-else-if="results[r.name]?.backtracking" class="field-hint"
            >Needs backtracking: matched with a time limit.</small
          >
        </div>
        <div v-if="r.pattern.trim()" class="field">
          <label :for="`tests-${r.name}`">Try values, one per line</label>
          <Textarea :id="`tests-${r.name}`" v-model="r.tests" rows="2" auto-resize class="mono" />
          <div v-for="m in results[r.name]?.results ?? []" :key="m.value" class="small mono">
            {{ m.matches === null ? '⏱' : m.matches ? '✓' : '✗' }} {{ m.value }}
          </div>
        </div>
      </div>
      <div v-if="r.mode === 'Exposed'" class="checks">
        <span class="check"
          ><Checkbox
            v-model="r.required"
            binary
            :input-id="`required-${r.name}`"
            :disabled="!canManage || r.parameter.mandatory"
          />
          <label :for="`required-${r.name}`">Required</label></span
        >
        <span v-if="r.kind === 'Text'" class="check"
          ><ToggleSwitch v-model="r.multiline" :input-id="`multiline-${r.name}`" :disabled="!canManage" />
          <label :for="`multiline-${r.name}`">Multi-line: line breaks and tabs allowed</label></span
        >
      </div>
      <div v-if="r.mode === 'Fixed'" class="field">
        <label :for="`fixed-${r.name}`">Fixed value</label>
        <ScriptArgumentInput
          :id="`fixed-${r.name}`"
          v-model="r.fixedValue"
          :parameter="r.parameter"
          :disabled="!canManage"
        />
      </div>
      <div v-if="r.mode === 'Exposed'" class="field">
        <label :for="`description-${r.name}`">Description for the caller</label>
        <InputText
          :id="`description-${r.name}`"
          v-model="r.description"
          :disabled="!canManage"
          :placeholder="r.parameter.helpMessage ?? ''"
        />
      </div>
    </div>
    <div class="actions">
      <Button
        v-if="canManage && rows.length"
        label="Check a call…"
        icon="pi pi-question-circle"
        text
        @click="checking = true"
      />
      <Button v-if="canManage" label="Save" icon="pi pi-check" :loading="busy" :disabled="!rows.length" @click="save" />
    </div>

    <Dialog
      v-model:visible="checking"
      header="Check a call"
      modal
      :style="{ width: '40rem' }"
      :breakpoints="{ '700px': '96vw' }"
    >
      <p class="small muted">
        The checks a call goes through, without running it and without the limits. Uses the saved arguments.
      </p>
      <div class="form">
        <div v-for="r in exposed" :key="r.name" class="field">
          <label :for="`sample-${r.name}`">{{ r.name }}</label>
          <ScriptArgumentInput
            :id="`sample-${r.name}`"
            v-model="samples[r.name]"
            :parameter="r.parameter"
            :multiline="r.multiline"
          />
        </div>
        <div class="field">
          <label for="sample-note">Note</label>
          <Textarea id="sample-note" v-model="note" rows="2" auto-resize />
        </div>
        <span class="check"
          ><Checkbox v-model="withReview" binary input-id="sample-review" :disabled="!connector.review.enabled" />
          <label for="sample-review">With the review (a real request to Jev, with its cost)</label></span
        >
      </div>
      <template v-if="checked">
        <Message :severity="checked.ok ? 'success' : 'error'" :closable="false" size="small">
          {{
            checked.ok
              ? `The arguments pass${checked.server ? `; it would run on ${checked.server.name}` : ''}.`
              : checked.message
          }}
          <div v-for="(m, f) in checked.fields" :key="f">{{ f }}: {{ m }}</div>
        </Message>
        <Message v-for="w in checked.warnings" :key="w.code" severity="warn" :closable="false" size="small">{{
          w.message
        }}</Message>
        <div v-if="checked.review" class="small">
          Review: {{ checked.review.state }}{{ checked.review.refusedBy ? ` by ${checked.review.refusedBy}` : '' }}
          <div v-for="a in checked.review.answers" :key="a.key">
            {{ a.label }}: {{ a.probability.toFixed(2) }}{{ a.refuses ? ' (refuses)' : '' }}
          </div>
        </div>
      </template>
      <template #footer>
        <Button label="Close" text severity="secondary" @click="checking = false" />
        <Button label="Check" icon="pi pi-play" :loading="busy" @click="check" />
      </template>
    </Dialog>
  </section>
</template>

<style scoped>
.argument {
  border-bottom: 1px solid var(--pnmt-border);
  padding: var(--pnmt-space-3) 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.head {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.head > :last-child {
  margin-left: auto;
}

.checks {
  display: flex;
  gap: var(--pnmt-space-4);
  flex-wrap: wrap;
}

.check {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--pnmt-space-2);
  margin-top: var(--pnmt-space-3);
}

.error {
  color: var(--pnmt-fail);
}

.small {
  font-size: 0.857rem;
}
</style>
