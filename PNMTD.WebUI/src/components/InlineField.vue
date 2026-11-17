<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Textarea from 'primevue/textarea'
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, nextTick, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { useNotify } from '@/composables/useNotify'

type Value = string | number | boolean | null

/**
 * A value edited where it is shown (§5.7). It is shown as text (a switch as Yes or No); a click on
 * it (or its pencil) opens an editor with Save and Discard, and only Save stores the value: neither
 * a change nor leaving the field does. Enter saves (Ctrl/⌘+Enter in a multi-line field), Esc
 * discards. If the server rejects the value, the editor stays open with it and the reason
 * underneath (or in a toast, when it is not about a field). A `custom` field shows its value as
 * text and edits a draft of the parent's in the #editor slot: `edit` asks the parent to start it,
 * and Save calls save, which stores that draft (docs/ssl-certificate-plan.md §8.3).
 */
const props = withDefaults(
  defineProps<{
    value: Value
    save: (value: Value) => Promise<unknown>
    type?: 'text' | 'textarea' | 'number' | 'bool' | 'enum' | 'custom'
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

const emit = defineEmits<{ edit: [] }>()
const notify = useNotify()
const shown = ref<Value>(props.value)
const editing = ref(false)
const saving = ref(false)
const error = ref<string | null>(null)
// The draft, by type: text, textarea and number; a choice; a switch.
const text = ref('')
const picked = ref<string | number | null>(null)
const flag = ref(false)

const root = ref<HTMLElement | null>(null)
const shownButton = ref<HTMLButtonElement | null>(null)
const input = ref<{ $el?: HTMLElement } | null>(null)
const choice = ref<{ show: (focus?: boolean) => void; overlayVisible?: boolean } | null>(null)

watch(
  () => props.value,
  (value) => {
    if (!saving.value) {
      shown.value = value
    }
  },
)

const display = computed(() => {
  if (props.type === 'bool') {
    return shown.value ? 'Yes' : 'No'
  }
  if (props.type === 'enum') {
    return props.options.find((o) => o.value === shown.value)?.label ?? String(shown.value ?? '')
  }
  return shown.value === null || shown.value === '' ? '' : String(shown.value)
})

const saveHint = computed(() => (props.type === 'textarea' ? 'Save (Ctrl+Enter)' : 'Save (Enter)'))

async function begin() {
  if (props.readonly || editing.value) {
    return
  }
  text.value = shown.value === null ? '' : String(shown.value)
  picked.value = typeof shown.value === 'string' || typeof shown.value === 'number' ? shown.value : null
  flag.value = Boolean(shown.value)
  error.value = null
  if (props.type === 'custom') {
    emit('edit')
  }
  editing.value = true
  await nextTick()
  if (props.type === 'enum') {
    choice.value?.show(true)
    return
  }
  if (props.type === 'custom') {
    root.value?.querySelector<HTMLElement>('.custom input, .custom [tabindex="0"]')?.focus()
    return
  }
  const el = input.value?.$el
  const field =
    el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement
      ? el
      : el?.querySelector<HTMLInputElement | HTMLTextAreaElement>('input, textarea')
  field?.focus()
  if (props.type !== 'bool') {
    field?.select()
  }
}

/** Back to the text; the focus too, if it was in the editor (keyboard use). */
async function close() {
  const hadFocus = root.value?.contains(document.activeElement) ?? false
  editing.value = false
  error.value = null
  if (hadFocus) {
    await nextTick()
    shownButton.value?.focus()
  }
}

function draftValue(): Value {
  switch (props.type) {
    case 'bool':
      return flag.value
    case 'enum':
      return picked.value
    case 'custom':
      return shown.value
    case 'number': {
      const trimmed = text.value.trim()
      return trimmed === '' ? null : Number(trimmed)
    }
    default:
      return text.value
  }
}

async function saveDraft() {
  if (saving.value) {
    return
  }
  const next = draftValue()
  if (props.type === 'number' && Number.isNaN(next)) {
    error.value = 'A number.'
    return
  }
  if (next === shown.value && props.type !== 'custom') {
    await close()
    return
  }
  saving.value = true
  error.value = null
  try {
    await props.save(next)
    // A custom field's text is the parent's: what it shows after the save.
    shown.value = props.type === 'custom' ? props.value : next
    await close()
  } catch (e) {
    // About this field, or about another one this value needs (a type that needs an interval).
    const message =
      e instanceof ApiError
        ? ((props.field ? e.field(props.field) : undefined) ?? Object.values(e.errors).flat()[0])
        : undefined
    if (message) {
      error.value = message
    } else {
      notify.error(e, `${props.label} not saved`)
    }
  } finally {
    saving.value = false
  }
}

// In the capture phase: the Select stops Esc itself. While its list is open, Enter and Esc are its
// own (pick, close); the buttons keep theirs.
function onKey(event: KeyboardEvent) {
  if (event.key !== 'Enter' && event.key !== 'Escape') {
    return
  }
  if ((event.target as HTMLElement | null)?.closest('.actions')) {
    return
  }
  if (props.type === 'enum' && choice.value?.overlayVisible) {
    return
  }
  // A custom editor's selects open and pick with Enter and close with Esc themselves.
  if (props.type === 'custom' && (event.target as HTMLElement | null)?.closest('[role="combobox"]')) {
    return
  }
  if (event.key === 'Enter' && props.type === 'textarea' && !event.ctrlKey && !event.metaKey) {
    return
  }
  event.preventDefault()
  event.stopPropagation()
  void (event.key === 'Enter' ? saveDraft() : close())
}
</script>

<template>
  <div ref="root" class="inline-field" :class="{ readonly }">
    <div v-if="editing" class="edit" :class="{ multiline: type === 'textarea' }" @keydown.capture="onKey">
      <Select
        v-if="type === 'enum'"
        ref="choice"
        v-model="picked"
        :options="options"
        option-label="label"
        option-value="value"
        size="small"
        class="editor"
        :disabled="saving"
        :aria-label="label"
      />
      <div v-else-if="type === 'custom'" class="editor custom"><slot name="editor" /></div>
      <ToggleSwitch v-else-if="type === 'bool'" ref="input" v-model="flag" :disabled="saving" :aria-label="label" />
      <Textarea
        v-else-if="type === 'textarea'"
        ref="input"
        v-model="text"
        auto-resize
        rows="3"
        class="editor"
        :disabled="saving"
        :aria-label="label"
      />
      <InputText
        v-else
        ref="input"
        v-model="text"
        size="small"
        class="editor"
        :class="{ mono }"
        :type="type === 'number' ? 'number' : 'text'"
        :disabled="saving"
        :aria-label="label"
      />
      <span class="actions">
        <Button
          v-tooltip.top="saveHint"
          icon="pi pi-check"
          size="small"
          :loading="saving"
          :aria-label="`Save ${label}`"
          @click="saveDraft"
        />
        <Button
          v-tooltip.top="'Discard (Esc)'"
          icon="pi pi-times"
          size="small"
          severity="secondary"
          text
          :disabled="saving"
          :aria-label="`Discard ${label}`"
          @click="close"
        />
      </span>
    </div>
    <button
      v-else
      ref="shownButton"
      type="button"
      class="shown"
      :class="{
        mono,
        empty: display === '',
        multiline: type === 'textarea',
        choice: type === 'enum' || type === 'bool',
      }"
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

.shown.choice {
  cursor: pointer;
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

.edit {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  min-height: 28px;
}

.edit.multiline {
  align-items: flex-start;
}

.editor {
  flex: 1;
  min-width: 0;
}

.custom {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.actions {
  display: inline-flex;
  flex: none;
  gap: var(--pnmt-space-1);
}

.mono {
  font-family: var(--pnmt-mono);
}
</style>
