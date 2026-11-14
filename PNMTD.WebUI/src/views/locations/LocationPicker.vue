<script setup lang="ts">
import Select from 'primevue/select'
import { computed } from 'vue'
import { pickerOptions } from './locationTree'
import { useLocations } from './useLocations'

/**
 * A location picker (docs/locations-knowledge-plan.md §3.2): search by path, pick, or *None*. <code>exclude</code> leaves a
 * location and its sub-locations out, where a move can't go (§3.3). <code>noneLabel</code> names the null choice (*None*,
 * *Top level*, *All locations*); <code>withUnassigned</code> adds a choice for hosts without a location, as the value
 * <code>none</code>, for filters.
 */
const model = defineModel<string | null>({ default: null })
const props = withDefaults(
  defineProps<{
    exclude?: string | null
    noneLabel?: string
    withUnassigned?: boolean
    inputId?: string
    ariaLabel?: string
    size?: 'small' | 'large'
    invalid?: boolean
  }>(),
  {
    exclude: null,
    noneLabel: 'None',
    withUnassigned: false,
    inputId: undefined,
    ariaLabel: 'Location',
    size: undefined,
    invalid: false,
  },
)

const locations = useLocations()
const options = computed(() => [
  { value: null, label: props.noneLabel, name: props.noneLabel, depth: 0 },
  ...(props.withUnassigned
    ? [{ value: 'none', label: 'Without a location', name: 'Without a location', depth: 0 }]
    : []),
  ...pickerOptions(locations.data.value?.items ?? [], props.exclude),
])
</script>

<template>
  <Select
    v-model="model"
    :options="options"
    option-label="label"
    option-value="value"
    filter
    filter-placeholder="Search by path"
    :placeholder="noneLabel"
    :input-id="inputId"
    :aria-label="ariaLabel"
    :size="size"
    :invalid="invalid"
    :loading="locations.isPending.value"
    reset-filter-on-hide
    class="location-picker"
  >
    <template #option="{ option }">
      <span class="option" :style="{ paddingLeft: `${Math.max(0, option.depth - 1) * 0.9}rem` }">
        <i v-if="option.depth" class="pi pi-map-marker muted" aria-hidden="true" />
        <span :class="{ muted: !option.depth }">{{ option.depth ? option.name : option.label }}</span>
        <span v-if="option.depth > 1" class="path muted">{{ option.label }}</span>
      </span>
    </template>
  </Select>
</template>

<style scoped>
.location-picker {
  min-width: 12rem;
}

.option {
  display: inline-flex;
  align-items: baseline;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.option i {
  font-size: 0.8rem;
}

.path {
  font-size: 0.857rem;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
</style>
