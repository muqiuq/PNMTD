<script setup lang="ts">
import AutoComplete from 'primevue/autocomplete'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, ref } from 'vue'
import { api, type S } from '@/api/resources'
import type { FieldDefinition, LinkValue } from './fieldValues'

/**
 * The input of a short field's value, by its type (docs/locations-knowledge-plan.md §4.1): the JSON of
 * Contracts/Locations.cs in and out, null for empty. Markdown isn't edited here but on the editing page (§5.9).
 * <code>users</code> are the users a User field can name; a Host field searches the hosts.
 */
const model = defineModel<unknown>({ required: true })
const props = withDefaults(
  defineProps<{
    field: Pick<FieldDefinition, 'type' | 'options' | 'label'>
    users?: readonly S['Ref'][] | null
    /** A Host or User value's name, to show before anything is searched. */
    target?: S['Ref'] | null
    disabled?: boolean
    inputId?: string
  }>(),
  { users: null, target: null, disabled: false, inputId: undefined },
)

const text = computed({
  get: () => (typeof model.value === 'string' ? model.value : ''),
  set: (v: string) => (model.value = v === '' ? null : v),
})
const number = computed({
  get: () => (typeof model.value === 'number' ? model.value : null),
  set: (v: number | null) => (model.value = v),
})
const yesNo = [
  { label: 'Yes', value: true },
  { label: 'No', value: false },
]
const choices = computed(() => props.field.options?.choices ?? [])
const picked = computed({
  get: () => (Array.isArray(model.value) ? (model.value as string[]) : []),
  set: (v: string[]) => (model.value = v.length ? v : null),
})
const link = computed<LinkValue>(() =>
  model.value && typeof model.value === 'object' ? (model.value as LinkValue) : { url: '', label: null },
)
function setLink(patch: Partial<LinkValue>) {
  const next = { ...link.value, ...patch }
  model.value = next.url || next.label ? { url: next.url, label: next.label || null } : null
}

// A host, searched by name; the value is its id.
const hostOptions = ref<S['Ref'][]>([])
const host = computed<S['Ref'] | null>({
  get: () =>
    typeof model.value === 'string' && model.value
      ? (hostOptions.value.find((h) => h.id === model.value) ??
        (props.target?.id === model.value ? props.target : { id: model.value, name: model.value }))
      : null,
  set: (v) => (model.value = v && typeof v === 'object' ? v.id : null),
})
async function searchHosts(event: { query: string }) {
  const page = await api.hosts.list({ search: event.query.trim() || undefined, pageSize: 20, sort: 'name' })
  hostOptions.value = page.items.map((h) => ({ id: h.id, name: h.name }))
}
</script>

<template>
  <div class="field-value-editor">
    <InputNumber
      v-if="field.type === 'Number'"
      v-model="number"
      :input-id="inputId"
      :min="field.options?.min ?? undefined"
      :max="field.options?.max ?? undefined"
      :max-fraction-digits="field.options?.decimals ?? 6"
      :suffix="field.options?.unit ? ` ${field.options.unit}` : undefined"
      :use-grouping="false"
      :disabled="disabled"
      size="small"
      fluid
      :aria-label="field.label"
    />
    <SelectButton
      v-else-if="field.type === 'YesNo'"
      v-model="model"
      :options="yesNo"
      option-label="label"
      option-value="value"
      :disabled="disabled"
      size="small"
      :aria-label="field.label"
    />
    <InputText
      v-else-if="field.type === 'Date'"
      :id="inputId"
      v-model="text"
      type="date"
      :disabled="disabled"
      size="small"
      :aria-label="field.label"
    />
    <Select
      v-else-if="field.type === 'Choice'"
      v-model="model"
      :options="choices"
      :input-id="inputId"
      show-clear
      :disabled="disabled"
      size="small"
      fluid
      :aria-label="field.label"
    />
    <MultiSelect
      v-else-if="field.type === 'Choices'"
      v-model="picked"
      :options="choices"
      :input-id="inputId"
      display="chip"
      :disabled="disabled"
      size="small"
      fluid
      :aria-label="field.label"
    />
    <div v-else-if="field.type === 'Link'" class="link">
      <InputText
        :id="inputId"
        :model-value="link.url"
        type="url"
        placeholder="https://"
        :disabled="disabled"
        size="small"
        :aria-label="`${field.label}: address`"
        @update:model-value="(v: string | undefined) => setLink({ url: (v ?? '').trim() })"
      />
      <InputText
        :model-value="link.label ?? ''"
        placeholder="Label (optional)"
        maxlength="128"
        :disabled="disabled"
        size="small"
        :aria-label="`${field.label}: label`"
        @update:model-value="(v: string | undefined) => setLink({ label: v ?? '' })"
      />
    </div>
    <Select
      v-else-if="field.type === 'User'"
      v-model="model"
      :options="users ? [...users] : target ? [target] : []"
      option-label="name"
      option-value="id"
      :input-id="inputId"
      filter
      show-clear
      :disabled="disabled"
      size="small"
      fluid
      :placeholder="target?.name"
      :aria-label="field.label"
    />
    <AutoComplete
      v-else-if="field.type === 'Host'"
      v-model="host"
      :suggestions="hostOptions"
      option-label="name"
      dropdown
      force-selection
      :input-id="inputId"
      :disabled="disabled"
      size="small"
      fluid
      placeholder="Search hosts"
      :aria-label="field.label"
      @complete="searchHosts"
    />
    <InputText
      v-else
      :id="inputId"
      v-model="text"
      :type="field.type === 'Email' ? 'email' : field.type === 'Phone' ? 'tel' : 'text'"
      :maxlength="field.type === 'Phone' ? 32 : field.type === 'Email' ? 254 : 512"
      :disabled="disabled"
      size="small"
      fluid
      :aria-label="field.label"
    />
  </div>
</template>

<style scoped>
.field-value-editor {
  flex: 1;
  min-width: 0;
}

.link {
  display: grid;
  grid-template-columns: minmax(0, 3fr) minmax(0, 2fr);
  gap: var(--pnmt-space-1);
}
</style>
