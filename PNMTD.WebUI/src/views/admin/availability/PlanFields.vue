<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import type { S } from '@/api/resources'
import ColorField from '@/components/ColorField.vue'
import CodeChip from './CodeChip.vue'
import DefaultWeekEditor from './DefaultWeekEditor.vue'
import type { PlanDraft } from './plan'

/**
 * A person's place in the availability plan (docs/cookie-plan.md §8.1): the plan's People tab and Admin → Users. With the
 * share as the day's responsible (docs/availability-extensions-plan.md §8), named as the settings call the responsible.
 */
withDefaults(
  defineProps<{
    types: readonly S['AvailabilityTypeItem'][]
    /** The server's messages by field (shortCode, color, defaultWeek, planOrder, responsibleShare). */
    errors: Record<string, string | undefined>
    idPrefix: string
    /** What the settings call the responsible (§7). */
    label?: string
  }>(),
  { label: 'Responsible' },
)
const draft = defineModel<PlanDraft>({ required: true })
const upper = (value: string | undefined) => (draft.value.shortCode = (value ?? '').toUpperCase())
</script>

<template>
  <div class="plan-fields">
    <span class="checkbox">
      <Checkbox v-model="draft.inPlan" binary :input-id="`${idPrefix}-in-plan`" />
      <label :for="`${idPrefix}-in-plan`">In the availability plan: a column of their own</label>
    </span>
    <div class="form-grid">
      <div class="field">
        <label :for="`${idPrefix}-code`">Short code</label>
        <span class="with-chip">
          <InputText
            :id="`${idPrefix}-code`"
            :model-value="draft.shortCode"
            maxlength="3"
            class="code mono"
            placeholder="NME"
            :invalid="!!errors.shortCode"
            @update:model-value="upper"
          />
          <CodeChip :code="draft.shortCode" :colour="draft.color" />
        </span>
        <small v-if="errors.shortCode" class="field-error">{{ errors.shortCode }}</small>
        <small v-else class="field-hint">3 letters, unique. The plan shows the person by it.</small>
      </div>
      <div class="field">
        <label :for="`${idPrefix}-colour`">Colour</label>
        <ColorField
          v-model="draft.color"
          :input-id="`${idPrefix}-colour`"
          label="Colour"
          clearable
          :invalid="!!errors.color"
        />
        <small v-if="errors.color" class="field-error">{{ errors.color }}</small>
      </div>
      <div class="field">
        <label :for="`${idPrefix}-order`">Column</label>
        <InputNumber
          v-model="draft.planOrder"
          :input-id="`${idPrefix}-order`"
          :min="0"
          :max="10000"
          :use-grouping="false"
          class="order"
          :invalid="!!errors.planOrder"
        />
        <small v-if="errors.planOrder" class="field-error">{{ errors.planOrder }}</small>
        <small v-else class="field-hint">The columns go from low to high.</small>
      </div>
      <div class="field">
        <label :for="`${idPrefix}-share`">Share as {{ label }}</label>
        <InputNumber
          v-model="draft.responsibleShare"
          :input-id="`${idPrefix}-share`"
          :min="0"
          :max="500"
          :use-grouping="false"
          suffix=" %"
          class="order"
          :invalid="!!errors.responsibleShare"
        />
        <small v-if="errors.responsibleShare" class="field-error">{{ errors.responsibleShare }}</small>
        <small v-else class="field-hint"
          >100 is a full share of the days as {{ label }}; 50 half as many as someone with 100; 0 never.</small
        >
      </div>
    </div>
    <div class="field">
      <span class="label">Default week</span>
      <DefaultWeekEditor v-model="draft.defaultWeek" :types="types" :id-prefix="`${idPrefix}-week`" />
      <small v-if="errors.defaultWeek" class="field-error">{{ errors.defaultWeek }}</small>
      <small v-else class="field-hint"
        >The fill writes it for the next 365 days, and never over what a person set.</small
      >
    </div>
  </div>
</template>

<style scoped>
.plan-fields {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.checkbox {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.with-chip {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.code {
  width: 5rem;
  text-transform: uppercase;
}

.order :deep(input) {
  width: 6rem;
}

.label {
  font-weight: 500;
}
</style>
