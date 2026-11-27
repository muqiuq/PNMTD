<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { daysUntil, isoDay, licenseKeys, licensesApi, money, uploadDocument, type LicenseStatus } from '@/api/licenses'
import { useNow } from '@/composables/useNow'
import { formatDay } from '@/format'
import { useNotify } from '@/composables/useNotify'
import DayField from './DayField.vue'

/**
 * Renew… (docs/licences-plan.md §5.6): the new expiry (the old one plus the term, or today plus the term when it expired more than a
 * term ago), the price from now on, the reference and a note, the paper as a document, a charge when PNMT records none, and
 * *Reactivate it* for a cancelled item. The sensors show 246 once.
 */
export interface RenewTarget {
  id: string
  display: string
  name: string
  expiresOn?: string | null
  termMonths?: number | null
  priceAmount?: number | null
  currency: string
  status: LicenseStatus
  /** Unknown (a list's row): PNMT records the charges, as by default. */
  recordCharges?: boolean
}

const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ item: RenewTarget }>()
const emit = defineEmits<{ done: [] }>()
const queryClient = useQueryClient()
const notify = useNotify()
const now = useNow()

function preset(): string {
  const term = props.item.termMonths ?? 12
  const today = new Date(now.value)
  const from =
    props.item.expiresOn && daysUntil(props.item.expiresOn, now.value) > -term * 30 ? props.item.expiresOn : null
  const base = from ? new Date(`${from}T12:00:00`) : today
  const next = new Date(base.getFullYear(), base.getMonth() + term, base.getDate() + (from ? 1 : 0) - 1, 12)
  return isoDay(next) ?? ''
}

const expires = ref<string | null>(null)
const price = ref<number | null>(null)
const reference = ref('')
const note = ref('')
const reactivate = ref(true)
const chargeAmount = ref<number | null>(null)
const chargeOn = ref<string | null>(null)
const paper = ref<File | null>(null)
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)
watch(visible, (open) => {
  if (!open) return
  expires.value = preset()
  price.value = props.item.priceAmount ?? null
  reference.value = ''
  note.value = ''
  reactivate.value = true
  chargeAmount.value = null
  chargeOn.value = null
  paper.value = null
  errors.value = {}
})
const recordsCharges = computed(() => props.item.recordCharges ?? true)

function pick(event: Event) {
  paper.value = (event.target as HTMLInputElement).files?.[0] ?? null
}

async function save() {
  saving.value = true
  try {
    const renewal = await licensesApi.renew(props.item.id, {
      newExpiresOn: expires.value,
      priceAmount: price.value,
      currency: props.item.currency,
      reference: reference.value.trim() || null,
      note: note.value.trim() || null,
      reactivate: props.item.status !== 'Active' && reactivate.value,
      chargeAmount: recordsCharges.value ? null : chargeAmount.value,
      chargeOn: recordsCharges.value ? null : chargeOn.value,
    })
    if (paper.value) {
      await uploadDocument(props.item.id, paper.value, paper.value.name, { renewalId: renewal.id }).catch(
        (e: unknown) => notify.error(e, 'Renewed, but the document wasn’t added'),
      )
    }
    await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
    notify.success(`${props.item.display} renewed until ${formatDay(renewal.newExpiresOn)}`)
    visible.value = false
    emit('done')
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length)
      errors.value = {
        newExpiresOn: e.field('newExpiresOn'),
        priceAmount: e.field('priceAmount'),
        reference: e.field('reference'),
        note: e.field('note'),
        chargeAmount: e.field('chargeAmount'),
      }
    else notify.error(e, 'Not renewed')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="`Renew ${item.display}`" modal :style="{ width: '32rem' }">
    <form id="licence-renew" class="form" novalidate @submit.prevent="save">
      <p class="muted">
        {{ item.name }}<template v-if="item.expiresOn">, now until {{ formatDay(item.expiresOn) }}</template
        >.
      </p>
      <div class="field">
        <label for="rn-expires">New expiry</label>
        <DayField v-model="expires" input-id="rn-expires" :invalid="!!errors.newExpiresOn" />
        <small v-if="errors.newExpiresOn" class="field-error">{{ errors.newExpiresOn }}</small>
      </div>
      <div class="field">
        <label for="rn-price">Price from now on ({{ item.currency }})</label>
        <InputNumber
          v-model="price"
          input-id="rn-price"
          :min-fraction-digits="2"
          :max-fraction-digits="2"
          :min="0"
          :invalid="!!errors.priceAmount"
          fluid
        />
        <small v-if="errors.priceAmount" class="field-error">{{ errors.priceAmount }}</small>
        <small v-else class="field-hint">A changed amount becomes the item's price and is kept with the renewal.</small>
      </div>
      <div class="field">
        <label for="rn-reference">Reference</label>
        <InputText
          id="rn-reference"
          v-model="reference"
          placeholder="Order or invoice number"
          :invalid="!!errors.reference"
        />
      </div>
      <div class="field">
        <label for="rn-note">Note</label>
        <InputText id="rn-note" v-model="note" :invalid="!!errors.note" />
      </div>
      <div class="field">
        <label for="rn-paper">The paper (an invoice, a quote)</label>
        <input id="rn-paper" type="file" @change="pick" />
      </div>
      <template v-if="!recordsCharges">
        <div class="field">
          <label for="rn-charge">Record a charge for it ({{ item.currency }})</label>
          <InputNumber
            v-model="chargeAmount"
            input-id="rn-charge"
            :min-fraction-digits="2"
            :max-fraction-digits="2"
            :min="0"
            :invalid="!!errors.chargeAmount"
            fluid
          />
          <small class="field-hint">PNMT doesn't record this item's charges itself. Leave empty for none.</small>
        </div>
        <div v-if="chargeAmount" class="field">
          <label for="rn-charge-on">Charged on</label>
          <DayField v-model="chargeOn" input-id="rn-charge-on" placeholder="Today" />
        </div>
      </template>
      <label v-if="item.status !== 'Active'" class="check">
        <Checkbox v-model="reactivate" binary input-id="rn-reactivate" />
        <span>Reactivate it</span>
      </label>
      <small v-if="price !== null && price !== item.priceAmount" class="field-hint"
        >From now on {{ money(price, item.currency) }} instead of {{ money(item.priceAmount, item.currency) }}.</small
      >
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="licence-renew" label="Renew" :loading="saving" data-test="renew-save" />
    </template>
  </Dialog>
</template>
