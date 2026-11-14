<script setup lang="ts">
import AutoComplete from 'primevue/autocomplete'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import FieldCard from '@/views/locations/FieldCard.vue'
import {
  fieldTypes,
  sampleTarget,
  sampleValue,
  type FieldDefinition,
  type FieldType,
} from '@/views/locations/fieldValues'

/**
 * A location field's definition (docs/locations-knowledge-plan.md §4.2, §10.5): label, type with its options, help,
 * group, and whether it is on the overview, inherited and sensitive; with a preview of how it will look. The key comes
 * from the first label and never changes. Choices keep where each came from, so a renamed one is renamed in every value
 * (§4.3); one still in use can't be removed, and the server names the locations.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = withDefaults(defineProps<{ field?: FieldDefinition | null; groups?: string[] }>(), {
  field: null,
  groups: () => [],
})
const emit = defineEmits<{ saved: [field: FieldDefinition] }>()

interface Choice {
  original: string | null
  value: string
}
const label = ref('')
const type = ref<FieldType>('Markdown')
const help = ref('')
const group = ref('')
const overview = ref(false)
const inherited = ref(false)
const sensitive = ref(false)
const choices = ref<Choice[]>([])
const unit = ref('')
const min = ref<number | null>(null)
const max = ref<number | null>(null)
const decimals = ref<number | null>(null)
const errors = ref<Record<string, string | undefined>>({})
const problem = ref<string | null>(null)

watch(
  visible,
  (open) => {
    if (!open) return
    const f = props.field
    label.value = f?.label ?? ''
    type.value = f?.type ?? 'Markdown'
    help.value = f?.help ?? ''
    group.value = f?.group ?? ''
    overview.value = f?.overview ?? false
    inherited.value = f?.inherited ?? false
    sensitive.value = f?.sensitive ?? false
    choices.value = (f?.options?.choices ?? []).map((c) => ({ original: c, value: c }))
    if (!choices.value.length) choices.value = [{ original: null, value: '' }]
    unit.value = f?.options?.unit ?? ''
    min.value = f?.options?.min ?? null
    max.value = f?.options?.max ?? null
    decimals.value = f?.options?.decimals ?? null
    errors.value = {}
    problem.value = null
  },
  { immediate: true },
)

const editing = computed(() => props.field !== null)
const hasChoices = computed(() => type.value === 'Choice' || type.value === 'Choices')
const typeHint = computed(() => fieldTypes.find((t) => t.value === type.value)?.hint)
const options = computed<S['LocationFieldOptions'] | null>(() =>
  hasChoices.value
    ? { choices: choices.value.map((c) => c.value.trim()).filter(Boolean) }
    : type.value === 'Number'
      ? { unit: unit.value.trim() || null, min: min.value, max: max.value, decimals: decimals.value }
      : null,
)
const request = computed<S['LocationFieldRequest']>(() => ({
  label: label.value.trim(),
  type: type.value,
  help: help.value.trim() || null,
  group: group.value.trim() || null,
  overview: overview.value,
  inherited: inherited.value,
  sensitive: sensitive.value,
  options: options.value,
  renames: hasChoices.value
    ? choices.value
        .filter((c) => c.original && c.value.trim() && c.original !== c.value.trim())
        .map((c) => ({ from: c.original, to: c.value.trim() }))
    : null,
}))

// The preview: the field as a location's page will show it, with an example value (§10.5).
const preview = computed<FieldDefinition>(() => ({
  id: props.field?.id ?? 'preview',
  key: props.field?.key ?? 'preview',
  label: label.value.trim() || 'New field',
  type: type.value,
  help: request.value.help ?? null,
  group: request.value.group ?? null,
  position: 0,
  overview: overview.value,
  inherited: inherited.value,
  sensitive: sensitive.value,
  options: options.value,
  archivedAt: null,
  createdAt: '',
  filled: 0,
}))
const previewValue = computed<S['LocationValueItem']>(() => ({
  fieldId: preview.value.id,
  value: sampleValue(preview.value),
  inheritedFrom: inherited.value ? { id: 'example', name: 'Zurich DC' } : null,
  revision: 0,
  updatedAt: null,
  updatedByName: null,
  target: sampleTarget(type.value),
}))

const groupSuggestions = ref<string[]>([])
const completeGroup = (event: { query: string }) =>
  (groupSuggestions.value = props.groups.filter((g) => g.toLowerCase().includes(event.query.toLowerCase())))

const { invalidate, notify, busy } = useActions()
async function save() {
  errors.value = { label: request.value.label ? undefined : 'A field needs a label.' }
  problem.value = null
  if (errors.value.label) return
  busy.value = true
  try {
    const saved = editing.value
      ? await api.locationFields.update(props.field!.id, request.value)
      : await api.locationFields.create(request.value)
    await invalidate([keys.locationFields, keys.locations])
    notify.success(editing.value ? `${saved.label} saved` : `${saved.label} added`)
    emit('saved', saved)
    visible.value = false
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = {
        label: e.field('label'),
        help: e.field('help'),
        group: e.field('group'),
        choices: e.field('options.choices') ?? e.field('renames'),
        number: e.field('options.unit') ?? e.field('options.max') ?? e.field('options.decimals'),
      }
    } else if (e instanceof ApiError && e.status === 409) {
      problem.value = e.message
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
    :header="editing ? `Edit ${field!.label}` : 'New location field'"
    modal
    :style="{ width: '52rem' }"
    :breakpoints="{ '959px': '95vw' }"
  >
    <div class="layout">
      <form id="field-form" class="form" novalidate @submit.prevent="save">
        <div class="field">
          <label for="field-label">Label</label>
          <InputText id="field-label" v-model="label" maxlength="64" autofocus :invalid="!!errors.label" />
          <small v-if="errors.label" class="field-error">{{ errors.label }}</small>
          <small v-else-if="editing" class="field-hint"
            >Key: <code>{{ field!.key }}</code>, never changed.</small
          >
        </div>
        <div class="field">
          <label for="field-type">Type</label>
          <Select v-model="type" :options="fieldTypes" option-label="label" option-value="value" input-id="field-type">
            <template #option="{ option }">
              <span class="type-option"><i :class="option.icon" aria-hidden="true" /> {{ option.label }}</span>
            </template>
          </Select>
          <small class="field-hint">
            {{ typeHint }}.
            <template v-if="editing && field!.filled">
              With values, the type changes only from Text to Markdown, from Choice to Choices, or from Choice to Text.
            </template>
          </small>
        </div>
        <div v-if="hasChoices" class="field">
          <label id="field-choices">Choices</label>
          <ul class="choices" aria-labelledby="field-choices">
            <li v-for="(choice, i) in choices" :key="i">
              <InputText v-model="choice.value" size="small" maxlength="64" :aria-label="`Choice ${i + 1}`" />
              <span v-if="choice.original && choice.original !== choice.value.trim()" class="muted renamed"
                >was {{ choice.original }}</span
              >
              <Button
                icon="pi pi-times"
                text
                rounded
                size="small"
                severity="secondary"
                :aria-label="`Remove choice ${i + 1}`"
                :disabled="choices.length === 1"
                @click="choices.splice(i, 1)"
              />
            </li>
          </ul>
          <Button
            label="Add a choice"
            icon="pi pi-plus"
            size="small"
            text
            :disabled="choices.length >= 50"
            @click="choices.push({ original: null, value: '' })"
          />
          <small v-if="errors.choices" class="field-error">{{ errors.choices }}</small>
          <small v-else class="field-hint"
            >Renaming one renames it in every value; one still in use can't be removed.</small
          >
        </div>
        <div v-if="type === 'Number'" class="number-options">
          <div class="field">
            <label for="field-unit">Unit</label>
            <InputText id="field-unit" v-model="unit" maxlength="16" placeholder="kW" />
          </div>
          <div class="field">
            <label for="field-min">Minimum</label>
            <InputNumber v-model="min" input-id="field-min" :max-fraction-digits="6" :use-grouping="false" />
          </div>
          <div class="field">
            <label for="field-max">Maximum</label>
            <InputNumber v-model="max" input-id="field-max" :max-fraction-digits="6" :use-grouping="false" />
          </div>
          <div class="field">
            <label for="field-decimals">Decimals</label>
            <InputNumber v-model="decimals" input-id="field-decimals" :min="0" :max="6" />
          </div>
          <small v-if="errors.number" class="field-error">{{ errors.number }}</small>
        </div>
        <div class="field">
          <label for="field-help">Help</label>
          <InputText id="field-help" v-model="help" maxlength="256" placeholder="Shown under the field while editing" />
          <small v-if="errors.help" class="field-error">{{ errors.help }}</small>
        </div>
        <div class="field">
          <label for="field-group">Group</label>
          <AutoComplete
            v-model="group"
            input-id="field-group"
            :suggestions="groupSuggestions"
            dropdown
            maxlength="64"
            placeholder="Access, Network, Contacts…"
            @complete="completeGroup"
          />
          <small v-if="errors.group" class="field-error">{{ errors.group }}</small>
          <small v-else class="field-hint">Groups the location's <i>Information</i> tab.</small>
        </div>
        <div class="switches">
          <div class="switch-row">
            <ToggleSwitch v-model="overview" input-id="field-overview" />
            <label for="field-overview">On the overview</label>
            <small class="muted">The right side of a location's page.</small>
          </div>
          <div class="switch-row">
            <ToggleSwitch v-model="inherited" input-id="field-inherited" />
            <label for="field-inherited">Inherited</label>
            <small class="muted">Sub-locations without a value show their nearest ancestor's.</small>
          </div>
          <div class="switch-row">
            <ToggleSwitch v-model="sensitive" input-id="field-sensitive" />
            <label for="field-sensitive">Sensitive</label>
            <small class="muted">Its values never reach an AI or MCP.</small>
          </div>
        </div>
        <Message v-if="problem" severity="error" :closable="false" size="small">{{ problem }}</Message>
      </form>
      <aside class="preview" aria-label="Preview">
        <div class="preview-title muted">Preview</div>
        <div class="panel">
          <FieldCard :field="preview" :value="previewValue" preview />
        </div>
        <small v-if="preview.help" class="muted">{{ preview.help }}</small>
      </aside>
    </div>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="field-form" :label="editing ? 'Save' : 'Add field'" :loading="busy" />
    </template>
  </Dialog>
</template>

<style scoped>
.layout {
  display: grid;
  grid-template-columns: minmax(0, 3fr) minmax(0, 2fr);
  gap: var(--pnmt-space-4);
  align-items: start;
}

.type-option i {
  width: 1.2rem;
  color: var(--pnmt-muted);
}

.choices {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.choices li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.renamed {
  font-size: 0.857rem;
  white-space: nowrap;
}

.number-options {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: var(--pnmt-space-2);
}

.number-options .field-error {
  grid-column: 1 / -1;
}

.switches {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.switch-row {
  display: grid;
  grid-template-columns: auto auto minmax(0, 1fr);
  align-items: center;
  gap: var(--pnmt-space-2);
}

.preview {
  position: sticky;
  top: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.preview-title {
  font-size: 0.786rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  font-weight: 600;
}

@media (max-width: 959px) {
  .layout {
    grid-template-columns: minmax(0, 1fr);
  }

  .number-options {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
</style>
