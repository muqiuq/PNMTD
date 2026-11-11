<script setup lang="ts">
import InputText from 'primevue/inputtext'
import Textarea from 'primevue/textarea'
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, nextTick, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { useNotify } from '@/composables/useNotify'

type Value = string | number | boolean | null

/**
 * A value edited where it is shown (§5.7). Enter or leaving the field saves, Esc cancels; a
 * switch or a choice saves at once. The new value shows immediately; if the save fails it rolls
 * back, with the server's message under the field (validation) or a toast.
 */
const props = withDefaults(
  defineProps<{
    value: Value
    save: (value: Value) => Promise<unknown>
    type?: 'text' | 'textarea' | 'number' | 'bool' | 'enum'
    options?: { label: string; value: string | number }[]
    /** The JSON name of the field in validation errors. */
    field?: string
    label: string
    placeholder?: string
    /** Read-only for this role: shown, not editable. */
    readonly?: boolean
    mono?: boolean
  }>(),
  { type: 'text', options: () => [], field: undefined, placeholder: '–', readonly: false, mono: false },
)

const notify = useNotify()
const shown = ref<Value>(props.value)
const draft = ref<string>('')
const editing = ref(false)
const saving = ref(false)
const error = ref<string | null>(null)
const input = ref<{ $el?: HTMLElement } | null>(null)

watch(
  () => props.value,
  (value) => {
    if (!saving.value) {
      shown.value = value
    }
  },
)

const display = computed(() => {
  if (props.type === 'enum') {
    return props.options.find((o) => o.value === shown.value)?.label ?? String(shown.value ?? '')
  }
  return shown.value === null || shown.value === '' ? '' : String(shown.value)
})

async function begin() {
  if (props.readonly || editing.value) {
    return
  }
  draft.value = shown.value === null ? '' : String(shown.value)
  error.value = null
  editing.value = true
  await nextTick()
  const el = input.value?.$el
  const field =
    el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement
      ? el
      : el?.querySelector<HTMLInputElement | HTMLTextAreaElement>('input, textarea')
  field?.focus()
  field?.select()
}

function cancel() {
  editing.value = false
  error.value = null
}

function parse(text: string): Value {
  if (props.type === 'number') {
    const trimmed = text.trim()
    return trimmed === '' ? null : Number(trimmed)
  }
  return text
}

async function commit(next: Value) {
  const previous = shown.value
  editing.value = false
  if (next === previous || (props.type === 'number' && Number.isNaN(next))) {
    return
  }
  shown.value = next
  saving.value = true
  error.value = null
  try {
    await props.save(next)
  } catch (e) {
    shown.value = previous
    const message = e instanceof ApiError && props.field ? e.field(props.field) : undefined
    if (message) {
      error.value = message
    } else {
      notify.error(e, `${props.label} not saved`)
    }
  } finally {
    saving.value = false
  }
}

function onKey(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    event.preventDefault()
    cancel()
  } else if (event.key === 'Enter' && (props.type !== 'textarea' || event.ctrlKey || event.metaKey)) {
    event.preventDefault()
    void commit(parse(draft.value))
  }
}

function onBlur() {
  if (editing.value) {
    void commit(parse(draft.value))
  }
}
</script>

<template>
  <div class="inline-field" :class="{ saving, readonly }">
    <template v-if="type === 'bool'">
      <ToggleSwitch
        :model-value="Boolean(shown)"
        :disabled="readonly || saving"
        :aria-label="label"
        @update:model-value="(v: boolean) => commit(v)"
      />
    </template>
    <template v-else-if="type === 'enum'">
      <Select
        v-if="!readonly"
        :model-value="shown"
        :options="options"
        option-label="label"
        option-value="value"
        size="small"
        :disabled="saving"
        :aria-label="label"
        @update:model-value="(v: string | number) => commit(v)"
      />
      <span v-else>{{ display }}</span>
    </template>
    <template v-else-if="editing">
      <Textarea
        v-if="type === 'textarea'"
        ref="input"
        v-model="draft"
        auto-resize
        rows="3"
        class="editor"
        :aria-label="label"
        @keydown="onKey"
        @blur="onBlur"
      />
      <InputText
        v-else
        ref="input"
        v-model="draft"
        size="small"
        class="editor"
        :class="{ mono }"
        :type="type === 'number' ? 'number' : 'text'"
        :aria-label="label"
        @keydown="onKey"
        @blur="onBlur"
      />
    </template>
    <button
      v-else
      type="button"
      class="shown"
      :class="{ mono, empty: display === '', multiline: type === 'textarea' }"
      :disabled="readonly"
      :aria-label="readonly ? undefined : `Edit ${label}`"
      @click="begin"
    >
      <span class="text">{{ display === '' ? placeholder : display }}</span>
      <i v-if="!readonly" class="pi pi-pencil" aria-hidden="true" />
    </button>
    <div v-if="error" class="field-error" role="alert">{{ error }}</div>
  </div>
</template>

<style scoped>
.inline-field {
  min-width: 0;
}

.shown {
  all: unset;
  box-sizing: border-box;
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  max-width: 100%;
  min-height: 28px;
  padding: 0 var(--pnmt-space-1);
  margin-left: calc(-1 * var(--pnmt-space-1));
  border-radius: var(--p-border-radius-sm);
  cursor: text;
}

.shown:not(:disabled):hover,
.shown:focus-visible {
  background: var(--p-content-hover-background);
}

.shown:focus-visible {
  outline: 1px solid var(--p-primary-color);
}

.shown:disabled {
  cursor: default;
}

.shown .text {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.shown.multiline .text {
  white-space: pre-wrap;
}

.shown i {
  font-size: 0.8rem;
  color: var(--pnmt-muted);
  opacity: 0;
}

.shown:hover i,
.shown:focus-visible i {
  opacity: 1;
}

.shown.empty .text {
  color: var(--pnmt-muted);
}

.editor {
  width: 100%;
}

.mono {
  font-family: var(--pnmt-mono);
}

.saving {
  opacity: 0.7;
}
</style>
