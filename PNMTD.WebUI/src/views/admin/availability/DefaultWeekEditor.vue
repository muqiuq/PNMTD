<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import Select from 'primevue/select'
import { computed } from 'vue'
import type { S } from '@/api/resources'

/**
 * A person's default week (docs/cookie-plan.md §8.1): for each weekday whether they usually work, and with which type when
 * it isn't the default (HOME on Fridays). The fill writes it; a closed day (a weekend, a holiday) stays empty anyway.
 */
const props = defineProps<{ types: readonly S['AvailabilityTypeItem'][]; idPrefix: string }>()
const week = defineModel<S['DefaultDay'][]>({ required: true })
const standard = computed(() => props.types.find((t) => t.isDefault))
const dayOff = computed(() => props.types.find((t) => t.isDayOff))
const options = computed(() => props.types.map((t) => ({ label: `${t.code} · ${t.name}`, value: t.id })))
</script>

<template>
  <div class="week" role="group" aria-label="Default week">
    <div v-for="d in week" :key="d.day" class="day">
      <span class="checkbox">
        <Checkbox v-model="d.works" binary :input-id="`${idPrefix}-${d.day}`" />
        <label :for="`${idPrefix}-${d.day}`">{{ d.day }}</label>
      </span>
      <Select
        v-if="d.works"
        v-model="d.typeId"
        :options="options"
        option-label="label"
        option-value="value"
        show-clear
        size="small"
        class="type"
        :placeholder="standard ? `${standard.code} (the default)` : 'The default'"
        :aria-label="`${d.day}, type`"
      />
      <span v-else class="muted small">{{ dayOff ? `off: ${dayOff.code}` : 'off' }}</span>
    </div>
  </div>
</template>

<style scoped>
.week {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.day {
  display: grid;
  grid-template-columns: 8.5rem minmax(0, 15rem);
  align-items: center;
  gap: var(--pnmt-space-2);
  min-height: 2.1rem;
}

.checkbox {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.small {
  font-size: 0.857rem;
}
</style>
