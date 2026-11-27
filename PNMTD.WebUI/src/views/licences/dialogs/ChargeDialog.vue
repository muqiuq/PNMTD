<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import {
  chargeKindLabels,
  licenseKeys,
  licensesApi,
  todayIso,
  uploadDocument,
  type LicenseCharge,
  type LicenseChargeKind,
} from '@/api/licenses'
import { useNotify } from '@/composables/useNotify'
import DayField from './DayField.vue'

/**
 * Record a cost… or a correction (docs/licences-plan.md §6.2, §6.3): the kind (an extra, a billing period, a credit, a purchase), the
 * day and the period, the amount and currency, the reference and a note, and the invoice as a document linked to the charge. A
 * credit is given positive and stored negative. PNMT's own charges keep their kind and stay automatic; entering the invoice's amount
 * clears *estimated*.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ licenseId: string; display: string; currency: string; charge?: LicenseCharge | null }>()
const emit = defineEmits<{ done: [charge: LicenseCharge] }>()
const queryClient = useQueryClient()
const notify = useNotify()

const kinds = (Object.keys(chargeKindLabels) as LicenseChargeKind[]).map((k) => ({
  value: k,
  label: chargeKindLabels[k],
}))
const kind = ref<LicenseChargeKind>('Extra')
const on = ref<string | null>(todayIso())
const from = ref<string | null>(null)
const to = ref<string | null>(null)
const amount = ref<number | null>(null)
const chargeCurrency = ref('')
const reference = ref('')
const note = ref('')
const estimated = ref(false)
const invoice = ref<File | null>(null)
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)
const automatic = computed(() => props.charge?.source === 'Automatic')
watch(visible, (open) => {
  if (!open) return
  const c = props.charge
  kind.value = c?.kind ?? 'Extra'
  on.value = c?.on ?? todayIso()
  from.value = c?.periodFrom ?? null
  to.value = c?.periodTo ?? null
  amount.value = c ? Math.abs(c.amount) : null
  chargeCurrency.value = c?.currency ?? props.currency
  reference.value = c?.reference ?? ''
  note.value = c?.note ?? ''
  estimated.value = c?.estimated ?? false
  invoice.value = null
  errors.value = {}
})

function pick(event: Event) {
  invoice.value = (event.target as HTMLInputElement).files?.[0] ?? null
}

async function save() {
  saving.value = true
  const body = {
    kind: kind.value,
    on: on.value,
    periodFrom: from.value,
    periodTo: to.value,
    amount: amount.value,
    currency: chargeCurrency.value.trim().toUpperCase() || null,
    reference: reference.value.trim() || null,
    note: note.value.trim() || null,
    estimated: props.charge
      ? estimated.value && amount.value === Math.abs(props.charge.amount)
        ? true
        : estimated.value
      : false,
  }
  try {
    const saved = props.charge
      ? await licensesApi.updateCharge(props.licenseId, props.charge.id, body)
      : await licensesApi.recordCharge(props.licenseId, body)
    if (invoice.value) {
      await uploadDocument(props.licenseId, invoice.value, invoice.value.name, {
        kind: 'Invoice',
        chargeId: saved.id,
      }).catch((e: unknown) => notify.error(e, 'Saved, but the invoice wasn’t added'))
    }
    await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
    notify.success(props.charge ? 'Charge corrected' : 'Cost recorded')
    visible.value = false
    emit('done', saved)
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length)
      errors.value = {
        amount: e.field('amount'),
        currency: e.field('currency'),
        periodTo: e.field('periodTo'),
        reference: e.field('reference'),
        note: e.field('note'),
        kind: e.field('kind'),
      }
    else notify.error(e, 'Not saved')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="charge ? `Correct a charge of ${display}` : `Record a cost for ${display}`"
    modal
    :style="{ width: '32rem' }"
  >
    <form id="licence-charge" class="form" novalidate @submit.prevent="save">
      <div class="field">
        <label for="lch-kind">Kind</label>
        <Select
          v-model="kind"
          input-id="lch-kind"
          :options="kinds"
          option-label="label"
          option-value="value"
          :disabled="automatic"
          fluid
        />
        <small v-if="automatic" class="field-hint">Recorded by PNMT for a billing period: it stays one.</small>
      </div>
      <div class="field">
        <label for="lch-on">Day</label>
        <DayField v-model="on" input-id="lch-on" />
      </div>
      <div class="row">
        <div class="field">
          <label for="lch-from">Period from</label>
          <DayField v-model="from" input-id="lch-from" :clearable="true" />
        </div>
        <div class="field">
          <label for="lch-to">to</label>
          <DayField v-model="to" input-id="lch-to" :clearable="true" :invalid="!!errors.periodTo" />
          <small v-if="errors.periodTo" class="field-error">{{ errors.periodTo }}</small>
        </div>
      </div>
      <div class="row">
        <div class="field grow">
          <label for="lch-amount">{{ kind === 'Credit' ? 'Amount back' : 'Amount' }}</label>
          <InputNumber
            v-model="amount"
            input-id="lch-amount"
            :min-fraction-digits="2"
            :max-fraction-digits="2"
            :min="0"
            :invalid="!!errors.amount"
            fluid
          />
          <small v-if="errors.amount" class="field-error">{{ errors.amount }}</small>
        </div>
        <div class="field currency">
          <label for="lch-currency">Currency</label>
          <InputText id="lch-currency" v-model="chargeCurrency" maxlength="3" :invalid="!!errors.currency" />
        </div>
      </div>
      <div class="field">
        <label for="lch-reference">Reference</label>
        <InputText
          id="lch-reference"
          v-model="reference"
          placeholder="The invoice number"
          :invalid="!!errors.reference"
        />
      </div>
      <div class="field">
        <label for="lch-note">Note</label>
        <InputText id="lch-note" v-model="note" :invalid="!!errors.note" />
      </div>
      <div class="field">
        <label for="lch-invoice">The invoice</label>
        <input id="lch-invoice" type="file" @change="pick" />
      </div>
      <label v-if="charge?.estimated" class="check">
        <Checkbox v-model="estimated" binary input-id="lch-estimated" />
        <span>Still an estimate</span>
      </label>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="licence-charge"
        :label="charge ? 'Save' : 'Record'"
        :loading="saving"
        data-test="charge-save"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.row {
  display: flex;
  gap: var(--pnmt-space-3);
}

.row > .field {
  flex: 1;
}

.currency {
  max-width: 7rem;
}
</style>
