<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import { isColour } from './colours'

/** A colour as #rrggbb: the browser's picker and the text, which the server checks; optional when clearable. */
const props = withDefaults(defineProps<{ label: string; clearable?: boolean; invalid?: boolean; inputId?: string }>(), {
  clearable: false,
  invalid: false,
  inputId: undefined,
})
const model = defineModel<string | null>({ required: true })
const picked = (e: Event) => (model.value = (e.target as HTMLInputElement).value.toLowerCase())
const typed = (value: string | undefined) =>
  (model.value = value?.trim() ? value.trim().toLowerCase() : props.clearable ? null : '')
</script>

<template>
  <span class="colour-field">
    <input
      type="color"
      class="swatch"
      :value="isColour(model) ? model : '#ffffff'"
      :aria-label="`${label}, picker`"
      @input="picked"
    />
    <InputText
      :id="inputId"
      :model-value="model ?? ''"
      size="small"
      class="hex mono"
      maxlength="7"
      placeholder="#rrggbb"
      :aria-label="label"
      :invalid="invalid || (!!model && !isColour(model))"
      @update:model-value="typed"
    />
    <Button
      v-if="clearable && model"
      icon="pi pi-times"
      size="small"
      severity="secondary"
      text
      :aria-label="`Clear ${label}`"
      @click="model = null"
    />
  </span>
</template>

<style scoped>
.colour-field {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.swatch {
  width: 2rem;
  height: 2rem;
  padding: 2px;
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-sm);
  background: var(--pnmt-panel);
  cursor: pointer;
}

.hex {
  width: 6.5rem;
}
</style>
