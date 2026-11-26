<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { useNotify } from '@/composables/useNotify'
import LocationPicker from '@/views/locations/LocationPicker.vue'
import { confidenceSeverity, endLabel } from './networkFormat'
import { blankDraft, check, draftOf, evidenceOptions, requestOf, type RuleDraft, type SkipRule } from './skipRules'

/**
 * A skip rule (docs/network-suggestion-rules-plan.md §4.5): new (blank, or `initial` from *Skip like this…*) or `rule` to
 * change. Its conditions must all match. Below, live from the stored scans, what it would change: the rows it skips, the
 * open suggestions that go or come back, the interfaces on what it skips (the preview, debounced, for a complete draft).
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{
  rule?: SkipRule | null
  initial?: RuleDraft | null
  types?: readonly string[]
}>()
const emit = defineEmits<{ saved: [rule: SkipRule] }>()

const PREVIEW_DELAY = 600
const notify = useNotify()
const { run } = useActions()
const draft = reactive<RuleDraft>(blankDraft())
const attempted = ref(false)
const serverErrors = ref<Record<string, string>>({})
const busy = ref(false)

const routers = useQuery({
  queryKey: [...keys.network, 'graph', 'routers'],
  queryFn: () => api.network.graph({ routers: 'all' }),
  enabled: computed(() => visible.value),
})
const routerOptions = computed(() =>
  (routers.data.value?.nodes ?? [])
    .filter((n) => n.kind === 'Router' && n.sensorId)
    .map((n) => ({ value: n.sensorId!, label: n.name }))
    .sort((x, y) => x.label.localeCompare(y.label)),
)
const typeOptions = computed(() =>
  [...new Set([...(props.types ?? []), ...draft.interfaceTypes])].sort().map((t) => ({ value: t, label: t })),
)
const scopes = [
  { value: 'All', label: 'All routers' },
  { value: 'Location', label: 'A location' },
  { value: 'Router', label: 'One router' },
]

const errors = computed(() => ({ ...check(draft), ...serverErrors.value }))
const errorOf = (field: string) => (attempted.value || field in serverErrors.value ? errors.value[field] : undefined)
const complete = computed(() => Object.keys(check(draft)).length === 0)

// --- the preview -----------------------------------------------------------------------------------------------------
const preview = ref<S['NetworkSkipRulePreview'] | null>(null)
const previewing = ref(false)
let timer: ReturnType<typeof setTimeout> | undefined
let sequence = 0
function stopPreview() {
  clearTimeout(timer)
  sequence++
  previewing.value = false
}
onBeforeUnmount(stopPreview)

watch(
  visible,
  (open) => {
    if (!open) {
      stopPreview()
      return
    }
    Object.assign(draft, props.rule ? draftOf(props.rule) : (props.initial ?? blankDraft()))
    attempted.value = false
    serverErrors.value = {}
    preview.value = null
  },
  { immediate: true },
)
watch(
  draft,
  () => {
    serverErrors.value = {}
    stopPreview()
    if (!visible.value || !complete.value) {
      preview.value = null
      return
    }
    const mine = ++sequence
    previewing.value = true
    timer = setTimeout(async () => {
      try {
        const result = await api.network.previewSkipRule({ id: props.rule?.id ?? null, rule: requestOf(draft) })
        if (mine === sequence) preview.value = result
      } catch {
        if (mine === sequence) preview.value = null
      } finally {
        if (mine === sequence) previewing.value = false
      }
    }, PREVIEW_DELAY)
  },
  { deep: true, immediate: true },
)

async function save() {
  attempted.value = true
  if (!complete.value) return
  busy.value = true
  try {
    const request = requestOf(draft)
    const saved = props.rule
      ? await api.network.updateSkipRule(props.rule.id, request)
      : await api.network.createSkipRule(request)
    notify.success(props.rule ? 'Skip rule saved' : 'Skip rule added: the suggestions follow in a few seconds')
    await run(async () => undefined, { invalidate: [keys.network] })
    emit('saved', saved)
    visible.value = false
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      serverErrors.value = Object.fromEntries(Object.entries(e.errors).map(([k, v]) => [k, v.join(' ')]))
    }
    notify.error(e, 'Not saved')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="rule ? 'Edit skip rule' : 'New skip rule'"
    modal
    :style="{ width: '40rem' }"
    :breakpoints="{ '599px': '95vw' }"
  >
    <div class="form">
      <p class="muted small">
        The analysis leaves out what the rule matches: those rows are no evidence for a suggestion, and a port its
        interface or type matches is no suggestion's end. What the routers saw stays, and no connection changes.
      </p>
      <div class="field">
        <label for="skip-name">Name</label>
        <InputText id="skip-name" v-model="draft.name" :invalid="!!errorOf('name')" placeholder="Guest VLAN" />
        <small v-if="errorOf('name')" class="field-error" role="alert">{{ errorOf('name') }}</small>
      </div>
      <div class="field">
        <label for="skip-scope">On</label>
        <Select
          v-model="draft.scope"
          input-id="skip-scope"
          :options="scopes"
          option-label="label"
          option-value="value"
          aria-label="On"
        />
        <LocationPicker
          v-if="draft.scope === 'Location'"
          v-model="draft.locationId"
          none-label="A location…"
          aria-label="Location"
          :invalid="!!errorOf('locationId')"
        />
        <Select
          v-if="draft.scope === 'Router'"
          v-model="draft.sensorId"
          :options="routerOptions"
          option-label="label"
          option-value="value"
          filter
          :loading="routers.isPending.value"
          placeholder="A router…"
          aria-label="Router"
          :invalid="!!errorOf('sensorId')"
        />
        <small v-if="draft.scope === 'Location'" class="field-hint">Its routers and those of its sub-locations.</small>
        <small v-if="errorOf('locationId') || errorOf('sensorId')" class="field-error" role="alert">{{
          errorOf('locationId') ?? errorOf('sensorId')
        }}</small>
      </div>
      <p class="muted small">Every condition you set must match.</p>
      <div class="grid">
        <div class="field">
          <label for="skip-interface">Interface</label>
          <InputText
            id="skip-interface"
            v-model="draft.interfacePattern"
            class="mono"
            placeholder="vlan9*, ether10, *-guest"
            :invalid="!!errorOf('interfacePattern')"
          />
          <small class="field-hint">As the router prints it, or the port under it. * and ? as wildcards.</small>
          <small v-if="errorOf('interfacePattern')" class="field-error" role="alert">{{
            errorOf('interfacePattern')
          }}</small>
        </div>
        <div class="field">
          <label for="skip-types">Type</label>
          <MultiSelect
            v-model="draft.interfaceTypes"
            input-id="skip-types"
            :options="typeOptions"
            option-label="label"
            option-value="value"
            filter
            display="chip"
            placeholder="Any type"
            aria-label="Interface types"
          />
        </div>
        <div class="field">
          <label for="skip-vlans">VLAN</label>
          <InputText id="skip-vlans" v-model="draft.vlans" placeholder="99, 4000-4094" :invalid="!!errorOf('vlans')" />
          <small v-if="errorOf('vlans')" class="field-error" role="alert">{{ errorOf('vlans') }}</small>
        </div>
        <div class="field">
          <label for="skip-evidence">Evidence</label>
          <MultiSelect
            v-model="draft.evidence"
            input-id="skip-evidence"
            :options="evidenceOptions"
            option-label="label"
            option-value="value"
            display="chip"
            placeholder="Any table"
            aria-label="Evidence"
          />
          <small class="field-hint">A neighbor seen by several protocols is skipped only when all of them are.</small>
        </div>
        <div class="field">
          <label for="skip-mac">MAC prefix</label>
          <InputText
            id="skip-mac"
            v-model="draft.macPrefix"
            class="mono"
            placeholder="00:50:56"
            :invalid="!!errorOf('macPrefix')"
          />
          <small v-if="errorOf('macPrefix')" class="field-error" role="alert">{{ errorOf('macPrefix') }}</small>
        </div>
        <div class="field">
          <label for="skip-enabled">Enabled</label>
          <div class="toggle">
            <ToggleSwitch v-model="draft.enabled" input-id="skip-enabled" />
            <span class="small">{{ draft.enabled ? 'The analysis applies it' : 'Off: kept, not applied' }}</span>
          </div>
        </div>
      </div>
      <small v-if="errorOf('scope')" class="field-error" role="alert">{{ errorOf('scope') }}</small>

      <section class="preview" aria-live="polite">
        <h3>What it changes, from the stored scans</h3>
        <p v-if="!complete" class="muted small">Complete the rule to see what it does.</p>
        <p v-else-if="previewing && !preview" class="muted small">Analysing…</p>
        <template v-else-if="preview">
          <p class="small">
            Skips <strong>{{ preview.rows }}</strong> row{{ preview.rows === 1 ? '' : 's' }} on
            {{ preview.routers }} router{{ preview.routers === 1 ? '' : 's' }}.
            <template v-if="preview.interfaces">
              {{ preview.interfaces }} network interface{{ preview.interfaces === 1 ? ' is' : 's are' }} attached by
              what it skips.
            </template>
          </p>
          <Message v-if="!preview.removedTotal && !preview.addedTotal" severity="secondary" size="small">
            It changes no open suggestion now; it applies to the next scans too.
          </Message>
          <template v-if="preview.removedTotal">
            <p class="small">
              Removes {{ preview.removedTotal }} open suggestion{{ preview.removedTotal === 1 ? '' : 's' }}:
            </p>
            <ul class="changes">
              <li v-for="s in preview.removed" :key="s.key">
                <Tag :value="s.confidence" :severity="confidenceSeverity(s.confidence)" class="small" />
                <span>{{ endLabel(s.a) }} ↔ {{ endLabel(s.b) }}</span>
              </li>
              <li v-if="preview.removedTotal > preview.removed.length" class="muted small">
                and {{ preview.removedTotal - preview.removed.length }} more
              </li>
            </ul>
          </template>
          <template v-if="preview.addedTotal">
            <p class="small">Brings back {{ preview.addedTotal }}:</p>
            <ul class="changes">
              <li v-for="s in preview.added" :key="s.key">
                <Tag :value="s.confidence" :severity="confidenceSeverity(s.confidence)" class="small" />
                <span>{{ endLabel(s.a) }} ↔ {{ endLabel(s.b) }}</span>
              </li>
              <li v-if="preview.addedTotal > preview.added.length" class="muted small">
                and {{ preview.addedTotal - preview.added.length }} more
              </li>
            </ul>
          </template>
        </template>
      </section>
    </div>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button :label="rule ? 'Save' : 'Add rule'" :loading="busy" @click="save" />
    </template>
  </Dialog>
</template>

<style scoped>
.form {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(16rem, 1fr));
  gap: var(--pnmt-space-3);
}

.field {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.toggle {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-height: 2.5rem;
}

.mono {
  font-family: var(--pnmt-mono);
}

.small {
  font-size: 0.857rem;
}

p {
  margin: 0;
}

.preview {
  border-top: 1px solid var(--pnmt-border);
  padding-top: var(--pnmt-space-3);
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.preview h3 {
  margin: 0;
  font-size: 1rem;
}

.changes {
  margin: 0;
  padding-left: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.changes li {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: baseline;
}
</style>
