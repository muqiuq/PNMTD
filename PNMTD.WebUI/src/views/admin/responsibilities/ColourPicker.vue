<script setup lang="ts">
import ColorField from '@/components/ColorField.vue'
import { palette, textOn } from '@/components/colours'

/**
 * A responsibility's colour (docs/responsibilities-extensions-plan.md §4.1): the twelve swatches, no colour, or a custom #rrggbb in the
 * field below them, which also shows the colour chosen.
 */
withDefaults(defineProps<{ inputId?: string; invalid?: boolean }>(), { inputId: undefined, invalid: false })
const model = defineModel<string | null>({ required: true })
</script>

<template>
  <div class="colour-picker">
    <div class="swatches" role="group" aria-label="Colour">
      <button
        v-for="s in palette"
        :key="s.hex"
        type="button"
        class="swatch"
        :style="{ background: s.hex }"
        :title="s.name"
        :aria-label="s.name"
        :aria-pressed="model === s.hex"
        :data-colour="s.hex"
        @click="model = s.hex"
      >
        <i v-if="model === s.hex" class="pi pi-check" :style="{ color: textOn(s.hex) }" aria-hidden="true" />
      </button>
      <button
        type="button"
        class="swatch none"
        title="No colour"
        aria-label="No colour"
        :aria-pressed="!model"
        data-colour="none"
        @click="model = null"
      >
        <i class="pi pi-ban" aria-hidden="true" />
      </button>
    </div>
    <ColorField v-model="model" label="Custom colour" clearable :invalid="invalid" :input-id="inputId" />
  </div>
</template>

<style scoped>
.colour-picker {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.swatches {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
}

.swatch {
  width: 1.6rem;
  height: 1.6rem;
  padding: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px solid var(--pnmt-border);
  border-radius: 50%;
  cursor: pointer;
  font-size: 0.75rem;
}

.swatch[aria-pressed='true'] {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 1px;
}

.swatch:focus-visible {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 2px;
}

.swatch.none {
  background: var(--pnmt-panel);
  color: var(--pnmt-muted);
  border-style: dashed;
}
</style>
