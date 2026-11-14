<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import type { S } from '@/api/resources'
import CodeChip from './CodeChip.vue'
import ColorField from './ColorField.vue'
import DefaultWeekEditor from './DefaultWeekEditor.vue'
import type { PlanDraft } from './plan'

/** A person's place in the availability plan (docs/cookie-plan.md §8.1): the plan's People tab and Admin → Users. */
defineProps<{
  types: readonly S['AvailabilityTypeItem'][]
  /** The server's messages by field (shortCode, color, defaultWeek, planOrder). */
  errors: Record<string, string | undefined>
  idPrefix: string
}>()
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
