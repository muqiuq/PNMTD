<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import {
  kindLabels,
  kindsOf,
  licenseKeys,
  licensesApi,
  typeLabels,
  vendorLabel,
  type LicenseTemplate,
  type LicenseType,
} from '@/api/licenses'
import { useNotify } from '@/composables/useNotify'
import { formatAbsolute, formatDay } from '@/format'
import DayField from './dialogs/DayField.vue'
import NameField from './NameField.vue'
import { endOfLifeChanged, templateDraft, templateRequest, type TemplateDraft } from './templates'

/**
 * A template (docs/licences-plan.md §3.2, §8.6): the product or service, its category, the defaults a new item starts with, its
 * support and its end of life. Read-only without Manage. Changing it changes no item's own fields; its end of life is the
 * exception, and the dialog says how many items follow it. *Make an item from it* is Edit.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ template: LicenseTemplate | null; type?: LicenseType; manage: boolean; edit: boolean }>()
const emit = defineEmits<{ make: [template: LicenseTemplate] }>()
const queryClient = useQueryClient()
const notify = useNotify()

const categories = useQuery({ queryKey: licenseKeys.categories, queryFn: licensesApi.categories, staleTime: 60_000 })
const draft = ref<TemplateDraft>(templateDraft(null))
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)
watch(visible, (open) => {
  if (!open) return
  draft.value = templateDraft(props.template, props.type ?? 'License')
  errors.value = {}
})
// A kind belongs to a type (§3.3): another type drops one it doesn't offer.
watch(
  () => draft.value.type,
  (type) => {
    if (draft.value.kind && !kindsOf[type].includes(draft.value.kind)) draft.value.kind = null
  },
)

const readonly = computed(() => !props.manage)
const creating = computed(() => !props.template)
const header = computed(() =>
  creating.value ? 'New template' : readonly.value ? props.template!.name : `Template ${props.template!.name}`,
)
const followers = computed(() => props.template?.followEndOfLife ?? 0)
const reevaluated = computed(() => followers.value > 0 && endOfLifeChanged(props.template, draft.value))

const types = (['License', 'Service', 'Hardware'] as LicenseType[]).map((t) => ({ value: t, label: typeLabels[t] }))
const kinds = computed(() => kindsOf[draft.value.type].map((k) => ({ value: k, label: kindLabels[k] })))
const categoryOptions = computed(() => (categories.data.value ?? []).map((c) => ({ value: c.id, label: c.name })))
const tri = [
  { value: true, label: 'Yes' },
  { value: false, label: 'No' },
]
const bases = [
  { value: 'Total', label: 'In total' },
  { value: 'PerSeat', label: 'Per seat' },
]
const billings = [
  { value: 'OneTime', label: 'One-time' },
  { value: 'Recurring', label: 'Recurring' },
  { value: 'Variable', label: 'Variable (by use)' },
]
const renewals = [
  { value: 'None', label: 'None: it ends' },
  { value: 'Manual', label: 'By hand (Renew…)' },
  { value: 'Automatic', label: 'Automatic, unless cancelled' },
]
const typeWord = computed(() => typeLabels[draft.value.type].toLowerCase())
const productLabel = computed(() => (draft.value.type === 'Hardware' ? 'Model' : 'Edition or version'))

async function save() {
  if (!props.manage) return
  saving.value = true
  try {
    const body = templateRequest(draft.value)
    if (props.template) await licensesApi.updateTemplate(props.template.id, body)
    else await licensesApi.createTemplate(body)
    await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
    notify.success(
      props.template ? 'Template saved' : 'Template created',
      reevaluated.value ? 'Its items are evaluated again.' : undefined,
    )
    visible.value = false
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length)
      errors.value = Object.fromEntries(Object.keys(e.errors).map((k) => [k, e.field(k)]))
    else if (e instanceof ApiError && e.code === 'duplicate_key') errors.value = { name: e.message }
    else notify.error(e, 'Not saved')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="header"
    modal
    :style="{ width: '52rem' }"
    :breakpoints="{ '960px': '95vw' }"
  >
    <form id="licence-template" class="form" novalidate @submit.prevent="save">
      <p v-if="readonly" class="muted">Reading only: changing templates is Licences & services: Manage.</p>
      <div class="form-grid">
        <div class="field wide">
          <label for="lt-name">Name</label>
          <InputText
            id="lt-name"
            v-model="draft.name"
            :disabled="readonly"
            :invalid="!!errors.name"
            placeholder="The product and edition, unless given: Windows Server 2016 Standard (16 cores)"
          />
          <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
        </div>
        <div class="field">
          <label for="lt-type">Type</label>
          <Select
            v-model="draft.type"
            input-id="lt-type"
            :options="types"
            option-label="label"
            option-value="value"
            :disabled="readonly"
            :invalid="!!errors.type"
            fluid
          />
        </div>
        <div class="field">
          <label for="lt-category">Category</label>
          <Select
            v-model="draft.categoryId"
            input-id="lt-category"
            :options="categoryOptions"
            option-label="label"
            option-value="value"
            placeholder="The type's default"
            show-clear
            :disabled="readonly"
            :invalid="!!errors.categoryId"
            fluid
          />
          <small v-if="errors.categoryId" class="field-error">{{ errors.categoryId }}</small>
        </div>
      </div>

      <h4>{{ draft.type === 'Service' ? 'The service' : 'The product' }}</h4>
      <div class="form-grid">
        <div class="field">
          <label for="lt-manufacturer">Manufacturer</label>
          <NameField
            v-model="draft.manufacturer"
            field="manufacturers"
            input-id="lt-manufacturer"
            :disabled="readonly"
            :invalid="!!errors.manufacturer"
            :placeholder="draft.type === 'Service' ? 'Optional for a service' : 'Microsoft, Veeam, Dell'"
          />
          <small v-if="errors.manufacturer" class="field-error">{{ errors.manufacturer }}</small>
        </div>
        <div class="field">
          <label for="lt-vendor">{{ vendorLabel(draft.type) }}</label>
          <NameField
            v-model="draft.vendor"
            field="vendors"
            input-id="lt-vendor"
            :disabled="readonly"
            :invalid="!!errors.vendor"
          />
          <small v-if="errors.vendor" class="field-error">{{ errors.vendor }}</small>
        </div>
        <div class="field">
          <label for="lt-product">Product name</label>
          <InputText id="lt-product" v-model="draft.productName" :disabled="readonly" :invalid="!!errors.productName" />
          <small v-if="errors.productName" class="field-error">{{ errors.productName }}</small>
        </div>
        <div class="field">
          <label for="lt-edition">{{ productLabel }}</label>
          <InputText id="lt-edition" v-model="draft.edition" :disabled="readonly" :invalid="!!errors.edition" />
          <small v-if="errors.edition" class="field-error">{{ errors.edition }}</small>
        </div>
        <div class="field wide">
          <label for="lt-url">Product page</label>
          <InputText
            id="lt-url"
            v-model="draft.productUrl"
            :disabled="readonly"
            :invalid="!!errors.productUrl"
            placeholder="https://… the manufacturer's page, for its lifecycle dates"
          />
          <small v-if="errors.productUrl" class="field-error">{{ errors.productUrl }}</small>
        </div>
      </div>

      <h4>Defaults for a new {{ typeWord }}</h4>
      <p class="field-hint">Empty fields are no default: the kind's defaults apply.</p>
      <div class="form-grid">
        <div class="field">
          <label for="lt-kind">Kind</label>
          <Select
            v-model="draft.kind"
            input-id="lt-kind"
            :options="kinds"
            option-label="label"
            option-value="value"
            placeholder="No default"
            show-clear
            :disabled="readonly"
            :invalid="!!errors.kind"
            fluid
          />
          <small v-if="errors.kind" class="field-error">{{ errors.kind }}</small>
        </div>
        <div class="field">
          <label for="lt-review">Review every (months)</label>
          <InputNumber
            v-model="draft.reviewMonths"
            input-id="lt-review"
            :min="1"
            :max="60"
            :use-grouping="false"
            :disabled="readonly"
            :invalid="!!errors.reviewMonths"
            fluid
          />
          <small v-if="errors.reviewMonths" class="field-error">{{ errors.reviewMonths }}</small>
        </div>
        <div class="field">
          <label for="lt-seats">Seats</label>
          <InputNumber
            v-model="draft.seats"
            input-id="lt-seats"
            :min="1"
            :max="1000000"
            :disabled="readonly"
            :invalid="!!errors.seats"
            fluid
          />
          <small v-if="errors.seats" class="field-error">{{ errors.seats }}</small>
        </div>
        <div class="field">
          <label for="lt-unit">Seat unit</label>
          <NameField
            v-model="draft.seatUnit"
            field="seatUnits"
            input-id="lt-unit"
            :disabled="readonly"
            :invalid="!!errors.seatUnit"
            placeholder="devices, users, cores"
          />
        </div>
        <div class="field">
          <label for="lt-from-hosts">Seats counted on hosts</label>
          <Select
            v-model="draft.seatsFromHosts"
            input-id="lt-from-hosts"
            :options="tri"
            option-label="label"
            option-value="value"
            placeholder="No default"
            show-clear
            :disabled="readonly"
            fluid
          />
        </div>
        <div class="field">
          <label for="lt-basis">The price is</label>
          <Select
            v-model="draft.priceBasis"
            input-id="lt-basis"
            :options="bases"
            option-label="label"
            option-value="value"
            placeholder="No default"
            show-clear
            :disabled="readonly"
            :invalid="!!errors.priceBasis"
            fluid
          />
        </div>
        <div class="field">
          <label for="lt-price">Price</label>
          <div class="pair">
            <InputNumber
              v-model="draft.priceAmount"
              input-id="lt-price"
              :min="0"
              :min-fraction-digits="2"
              :max-fraction-digits="2"
              :disabled="readonly"
              :invalid="!!errors.priceAmount"
              fluid
            />
            <InputText
              v-model="draft.currency"
              class="currency"
              maxlength="3"
              placeholder="EUR"
              aria-label="Currency"
              :disabled="readonly"
              :invalid="!!errors.currency"
            />
          </div>
          <small v-if="errors.priceAmount || errors.currency" class="field-error">{{
            errors.priceAmount ?? errors.currency
          }}</small>
        </div>
        <div class="field">
          <label for="lt-billing">Billing</label>
          <Select
            v-model="draft.billing"
            input-id="lt-billing"
            :options="billings"
            option-label="label"
            option-value="value"
            placeholder="No default"
            show-clear
            :disabled="readonly"
            :invalid="!!errors.billing"
            fluid
          />
        </div>
        <div v-if="draft.billing !== 'OneTime'" class="field">
          <label for="lt-billing-months">Billed every (months)</label>
          <InputNumber
            v-model="draft.billingMonths"
            input-id="lt-billing-months"
            :min="1"
            :max="120"
            :use-grouping="false"
            :disabled="readonly"
            :invalid="!!errors.billingMonths"
            fluid
          />
          <small v-if="errors.billingMonths" class="field-error">{{ errors.billingMonths }}</small>
        </div>
        <div class="field">
          <label for="lt-term">Term (months)</label>
          <InputNumber
            v-model="draft.termMonths"
            input-id="lt-term"
            :min="1"
            :max="120"
            :use-grouping="false"
            :disabled="readonly"
            :invalid="!!errors.termMonths"
            fluid
          />
          <small v-if="errors.termMonths" class="field-error">{{ errors.termMonths }}</small>
        </div>
        <div class="field">
          <label for="lt-renewal">Renewal</label>
          <Select
            v-model="draft.renewal"
            input-id="lt-renewal"
            :options="renewals"
            option-label="label"
            option-value="value"
            placeholder="No default"
            show-clear
            :disabled="readonly"
            fluid
          />
        </div>
        <div class="field">
          <label for="lt-warn">Warn days before it expires</label>
          <InputNumber
            v-model="draft.warnDays"
            input-id="lt-warn"
            :min="0"
            :max="730"
            :use-grouping="false"
            :disabled="readonly"
            :invalid="!!errors.warnDays"
            fluid
          />
          <small v-if="errors.warnDays" class="field-error">{{ errors.warnDays }}</small>
        </div>
        <template v-if="draft.renewal === 'Automatic'">
          <div class="field">
            <label for="lt-notice">Notice (days)</label>
            <InputNumber
              v-model="draft.noticeDays"
              input-id="lt-notice"
              :min="0"
              :max="730"
              :use-grouping="false"
              :disabled="readonly"
              :invalid="!!errors.noticeDays"
              fluid
            />
            <small v-if="errors.noticeDays" class="field-error">{{ errors.noticeDays }}</small>
          </div>
          <div class="field">
            <label for="lt-warn-renewal">Warn before it renews</label>
            <Select
              v-model="draft.warnBeforeRenewal"
              input-id="lt-warn-renewal"
              :options="tri"
              option-label="label"
              option-value="value"
              placeholder="No default"
              show-clear
              :disabled="readonly"
              fluid
            />
          </div>
        </template>
      </div>

      <h4>Support</h4>
      <div class="form-grid">
        <div class="field wide">
          <label for="lt-support">Support contact</label>
          <Textarea
            id="lt-support"
            v-model="draft.supportContact"
            rows="2"
            auto-resize
            :disabled="readonly"
            :invalid="!!errors.supportContact"
            placeholder="Phone, e-mail, hours"
          />
          <small v-if="errors.supportContact" class="field-error">{{ errors.supportContact }}</small>
        </div>
        <div class="field">
          <label for="lt-support-url">Support URL</label>
          <InputText
            id="lt-support-url"
            v-model="draft.supportUrl"
            :disabled="readonly"
            :invalid="!!errors.supportUrl"
          />
          <small v-if="errors.supportUrl" class="field-error">{{ errors.supportUrl }}</small>
        </div>
        <div class="field">
          <label for="lt-sla">SLA</label>
          <InputText
            id="lt-sla"
            v-model="draft.sla"
            :disabled="readonly"
            :invalid="!!errors.sla"
            placeholder="24/7, 4 h response"
          />
          <small v-if="errors.sla" class="field-error">{{ errors.sla }}</small>
        </div>
      </div>

      <h4>End of life</h4>
      <div class="form-grid">
        <div class="field">
          <label for="lt-eol">{{ draft.type === 'Service' ? 'End of service' : 'End of life' }}</label>
          <InputText
            v-if="readonly"
            id="lt-eol"
            :model-value="draft.endOfLifeOn ? formatDay(draft.endOfLifeOn) : '–'"
            disabled
          />
          <DayField v-else v-model="draft.endOfLifeOn" input-id="lt-eol" clearable :invalid="!!errors.endOfLifeOn" />
          <small v-if="errors.endOfLifeOn" class="field-error">{{ errors.endOfLifeOn }}</small>
        </div>
        <div class="field">
          <label for="lt-eol-note">What it is</label>
          <InputText
            id="lt-eol-note"
            v-model="draft.endOfLifeNote"
            :disabled="readonly"
            :invalid="!!errors.endOfLifeNote"
            placeholder="end of extended support"
          />
          <small v-if="errors.endOfLifeNote" class="field-error">{{ errors.endOfLifeNote }}</small>
        </div>
        <p v-if="reevaluated" class="notice wide" role="status" data-test="template-followers">
          <i class="pi pi-info-circle" aria-hidden="true" />
          {{
            followers === 1
              ? '1 item follows this end of life; it will be evaluated again.'
              : `${followers} items follow this end of life; they will be evaluated again.`
          }}
        </p>
        <p v-else-if="template && followers" class="field-hint wide">
          {{ followers === 1 ? '1 item follows' : `${followers} items follow` }} this end of life.
        </p>
      </div>

      <div class="field">
        <label for="lt-note">Note</label>
        <Textarea
          id="lt-note"
          v-model="draft.note"
          rows="3"
          auto-resize
          :disabled="readonly"
          :invalid="!!errors.note"
        />
        <small v-if="errors.note" class="field-error">{{ errors.note }}</small>
      </div>
      <p v-if="template" class="field-hint">
        {{ template.items === 1 ? '1 item made from it' : `${template.items} items made from it` }} · created
        {{ formatAbsolute(template.createdAt) }}{{ template.createdBy ? ` by ${template.createdBy}` : '' }}
      </p>
    </form>
    <template #footer>
      <Button
        v-if="template && edit"
        label="Make an item from it"
        icon="pi pi-plus-circle"
        severity="secondary"
        outlined
        class="make"
        @click="emit('make', template)"
      />
      <Button :label="manage ? 'Cancel' : 'Close'" severity="secondary" text @click="visible = false" />
      <Button
        v-if="manage"
        type="submit"
        form="licence-template"
        :label="template ? 'Save' : 'Create template'"
        :loading="saving"
        data-test="template-save"
      />
    </template>
  </Dialog>
</template>

<style scoped>
h4 {
  margin: var(--pnmt-space-2) 0 0;
  font-size: 0.95rem;
}

.pair {
  display: flex;
  gap: var(--pnmt-space-2);
}

.currency {
  width: 5rem;
  flex: none;
}

.notice {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  margin: 0;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-warn-soft);
}

.make {
  margin-right: auto;
}
</style>
