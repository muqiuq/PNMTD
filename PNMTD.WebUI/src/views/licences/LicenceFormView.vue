<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import {
  expiryLabel,
  kindLabels,
  kindsOf,
  licenseKeys,
  licensesApi,
  requestOf,
  todayIso,
  typeLabels,
  typeTabs,
  vendorLabel,
  type LicenseBilling,
  type LicenseKind,
  type LicenseRenewal,
  type LicenseRequest,
  type LicenseType,
} from '@/api/licenses'
import ErrorState from '@/components/ErrorState.vue'
import PageHeader from '@/components/PageHeader.vue'
import { useActions } from '@/composables/useActions'
import { formatDay } from '@/format'
import LocationPicker from '@/views/locations/LocationPicker.vue'
import DayField from './dialogs/DayField.vue'
import FormRow from './item/FormRow.vue'
import {
  addDays,
  blankRequest,
  credentialWarning,
  needsExpiry,
  pastCharges,
  seatsFromHostsDefault,
  templateName,
  withKind,
  withTemplate,
} from './item/item'
import OwnerPicker from './item/OwnerPicker.vue'
import NameField from './NameField.vue'

/**
 * The item's form (docs/licences-plan.md §3.3, §8.7): new (`?type=…&template=…`) or edit (by number), with the groups of its page,
 * shown for the type. A template fills in its defaults and product, and names the item; another kind fills in its defaults (§3.3).
 * The server's reasons show under their fields. A new item that started in the past with a recurring price is offered its charges
 * since then (§6.2), off by default. The keys are password fields: empty keeps them when editing. Saving a new item opens its page,
 * which then offers *Assign to hosts…*.
 */
const props = defineProps<{ number?: string }>()
const route = useRoute()
const router = useRouter()
const queryClient = useQueryClient()
const { ask, notify } = useActions()
const today = todayIso()
const editing = computed(() => !!props.number)

const queryType = computed<LicenseType>(() => {
  const t = route.query.type
  return t === 'Service' || t === 'Hardware' ? t : 'License'
})
const templateId = computed(() => (typeof route.query.template === 'string' ? route.query.template : null))

const existing = useQuery({
  queryKey: computed(() => licenseKeys.item(Number(props.number))),
  queryFn: () => licensesApi.byNumber(Number(props.number)),
  enabled: editing,
})
const current = computed(() => existing.data.value)
const template = useQuery({
  queryKey: computed(() => [...licenseKeys.templates, templateId.value]),
  queryFn: () => licensesApi.template(templateId.value!),
  enabled: computed(() => !editing.value && !!templateId.value),
})
const templates = useQuery({ queryKey: licenseKeys.templates, queryFn: licensesApi.templates, staleTime: 60_000 })
const categories = useQuery({ queryKey: licenseKeys.categories, queryFn: licensesApi.categories, staleTime: 60_000 })

// The draft: built once from the item, or from the type and template, then the person's.
const d = ref<LicenseRequest>(blankRequest(queryType.value, today))
const ready = ref(false)
const seatsTouched = ref(false)
watch(
  [() => existing.data.value, () => template.data.value, () => template.isError.value],
  ([item, t, failed]) => {
    if (ready.value) return
    if (editing.value) {
      if (!item) return
      // requestOf copies every value: the query's data stays as it is.
      d.value = requestOf(item)
      seatsTouched.value = true
    } else if (templateId.value) {
      if (!t && !failed) return
      d.value = t ? withTemplate(blankRequest(t.type, today), t, today) : blankRequest(queryType.value, today)
    }
    ready.value = true
  },
  { immediate: true },
)
const defaultCategory = (type: LicenseType) =>
  categories.data.value?.find((c) => c.defaultFor === type)?.id ?? categories.data.value?.[0]?.id ?? null
watch(
  [ready, () => categories.data.value],
  () => {
    if (ready.value && !d.value.categoryId) d.value.categoryId = defaultCategory(d.value.type ?? 'License')
  },
  { immediate: true },
)

const type = computed(() => d.value.type ?? 'License')
const kind = computed(() => d.value.kind ?? 'Other')

// --- the choices ------------------------------------------------------------------------------------------------------------
const typeOptions = (Object.keys(typeLabels) as LicenseType[]).map((t) => ({ label: typeLabels[t], value: t }))
const kindOptions = computed(() => kindsOf[type.value].map((k) => ({ label: kindLabels[k], value: k })))
const categoryOptions = computed(() => (categories.data.value ?? []).map((c) => ({ label: c.name, value: c.id })))
const templateOptions = computed(() =>
  [...(templates.data.value ?? [])]
    .sort((a, b) => (a.type === type.value ? 0 : 1) - (b.type === type.value ? 0 : 1) || a.name.localeCompare(b.name))
    .map((t) => ({
      label: t.type === type.value ? t.name : `${t.name} (${typeLabels[t.type].toLowerCase()})`,
      value: t.id,
    })),
)
const basisOptions = [
  { label: 'In total', value: 'Total' },
  { label: 'Per seat', value: 'PerSeat' },
]
const billingOptions: { label: string; value: LicenseBilling }[] = [
  { label: 'One-time', value: 'OneTime' },
  { label: 'Recurring', value: 'Recurring' },
  { label: 'Variable (paid by use)', value: 'Variable' },
]
const renewalOptions: { label: string; value: LicenseRenewal }[] = [
  { label: 'None: it ends', value: 'None' },
  { label: 'By hand (Renew…)', value: 'Manual' },
  { label: 'Automatically, unless cancelled', value: 'Automatic' },
]
const titleOfGroup = computed(() => ({ License: 'Product', Service: 'Service', Hardware: 'Hardware' })[type.value])
const showSupport = ref(false)
watch(
  [ready, type],
  () => {
    if (type.value === 'Service' || d.value.supportContact || d.value.supportUrl || d.value.sla)
      showSupport.value = true
  },
  { immediate: true },
)

// --- the changes that bring defaults (§3.3) ---------------------------------------------------------------------------------
function setType(next: LicenseType) {
  const before = d.value
  const wasDefault = !before.categoryId || before.categoryId === defaultCategory(before.type ?? 'License')
  let draft: LicenseRequest = { ...before, type: next }
  const k = before.kind ?? 'Other'
  if (!kindsOf[next].includes(k)) draft = withKind(draft, kindsOf[next][0], today)
  if (!editing.value && wasDefault && !draft.templateId) draft.categoryId = defaultCategory(next)
  if (!seatsTouched.value) draft.seatsFromHosts = seatsFromHostsDefault(next, draft.seatUnit)
  d.value = draft
}
function setKind(next: LicenseKind) {
  d.value = withKind(d.value, next, today)
}
function setUnit(unit: string | null | undefined) {
  d.value.seatUnit = unit
  if (!seatsTouched.value) d.value.seatsFromHosts = seatsFromHostsDefault(type.value, unit)
}
function setTemplate(id: string | null) {
  if (!id) {
    d.value = { ...d.value, templateId: null, endOfLifeFromTemplate: false }
    return
  }
  const t = templates.data.value?.find((x) => x.id === id)
  if (!t) return
  // A new item takes the template's defaults; an item kept takes only the template (its own fields were copied once, §8.6).
  d.value = editing.value ? { ...d.value, templateId: id } : withTemplate(d.value, t, today)
}
const pickedTemplate = computed(() => templates.data.value?.find((t) => t.id === d.value.templateId) ?? null)

// --- the charges since the start (§6.2) --------------------------------------------------------------------------------------
const past = computed(() => (editing.value ? 0 : pastCharges(d.value, today)))
watch(past, (n) => {
  if (!n) d.value.recordPastCharges = false
})

// --- saving -------------------------------------------------------------------------------------------------------------------
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)
const text = (v: string | null | undefined) => (v && v.trim() ? v.trim() : null)
function body(): LicenseRequest {
  const r = d.value
  return {
    ...r,
    name: text(r.name),
    licenseKey: r.licenseKey ? r.licenseKey : null,
    productKey: r.productKey ? r.productKey : null,
    seatsFromHosts: r.seats !== null && r.seats !== undefined && !!r.seatsFromHosts,
    billingMonths: r.billing === 'OneTime' ? null : r.billingMonths,
    noticeDays: r.renewal === 'Automatic' ? r.noticeDays : null,
    warnBeforeRenewal: r.renewal === 'Automatic' && !!r.warnBeforeRenewal,
    endOfLifeFromTemplate: !!r.templateId && !!r.endOfLifeFromTemplate,
    currency: text(r.currency)?.toUpperCase() ?? null,
    note: text(r.note) ? r.note : null,
    recordPastCharges: !editing.value && past.value > 0 && !!r.recordPastCharges,
    acknowledgeCredentials: false,
  }
}
async function send(request: LicenseRequest) {
  return editing.value ? licensesApi.update(current.value!.id, request) : licensesApi.create(request)
}
async function save() {
  saving.value = true
  errors.value = {}
  try {
    const request = body()
    let saved
    try {
      saved = await send(request)
    } catch (e) {
      if (!(e instanceof ApiError && e.code === 'looks_like_credential')) throw e
      if (!(await ask('This looks like a key', `${credentialWarning} Save anyway?`, 'Save anyway'))) {
        // The note or the support contact: the answer doesn't say which, so the summary does.
        errors.value = { credentials: `${credentialWarning} Not saved.` }
        return
      }
      saved = await send({ ...request, acknowledgeCredentials: true })
    }
    queryClient.setQueryData(licenseKeys.item(saved.number), saved)
    await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
    notify.success(editing.value ? `${saved.display} saved` : `${saved.display} created`)
    await router.push({
      name: 'licence',
      params: { number: String(saved.number) },
      query: editing.value ? {} : { assign: '1' },
    })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = Object.fromEntries(Object.entries(e.errors).map(([k, v]) => [k, v[0]]))
    } else notify.error(e, 'Not saved')
  } finally {
    saving.value = false
  }
}
const shownFields = [
  'type',
  'templateId',
  'name',
  'categoryId',
  'manufacturer',
  'productName',
  'edition',
  'vendor',
  'kind',
  'licensedTo',
  'reference',
  'customerNumber',
  'portalUrl',
  'supportContact',
  'supportUrl',
  'sla',
  'costCenter',
  'locationId',
  'ownerId',
  'licenseKey',
  'productKey',
  'seats',
  'seatUnit',
  'priceAmount',
  'currency',
  'priceBasis',
  'billing',
  'billingMonths',
  'startsOn',
  'expiresOn',
  'termMonths',
  'renewal',
  'warnDays',
  'noticeDays',
  'endOfLifeFromTemplate',
  'endOfLifeOn',
  'endOfLifeNote',
  'eolWarnDays',
  'reviewedOn',
  'reviewMonths',
  'note',
]
// A reason for a field the form doesn't show at the moment (support hidden, a renewal's notice).
const otherErrors = computed(() =>
  Object.entries(errors.value)
    .filter(
      ([k, v]) =>
        v && (!shownFields.includes(k) || (['supportContact', 'supportUrl', 'sla'].includes(k) && !showSupport.value)),
    )
    .map(([, v]) => v!),
)

function cancel() {
  if (editing.value) void router.push({ name: 'licence', params: { number: props.number } })
  else void router.push({ name: 'licences', params: { tab: typeTabs[type.value] } })
}
const title = computed(() =>
  editing.value
    ? `Edit ${current.value?.display ?? ''} · ${current.value?.name ?? ''}`
    : `New ${typeLabels[type.value].toLowerCase()}`,
)
</script>

<template>
  <div class="page">
    <PageHeader
      :title="title"
      :crumbs="[
        { label: 'Licences & services', to: { name: 'licences' } },
        ...(editing && current ? [{ label: current.display, to: { name: 'licence', params: { number } } }] : []),
      ]"
    />
    <ErrorState
      v-if="existing.isError.value"
      :error="existing.error.value"
      what="the licence or service"
      @retry="existing.refetch()"
    />
    <div v-else-if="!ready" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <form v-else class="licence-form" novalidate data-test="licence-form" @submit.prevent="save">
      <Message v-if="template.isError.value" severity="warn" :closable="false" size="small">
        The template couldn't be read; the form starts empty.
      </Message>
      <Message
        v-if="Object.keys(errors).length"
        severity="error"
        :closable="false"
        size="small"
        data-test="form-errors"
      >
        Some fields need a look.
        <template v-for="e in otherErrors" :key="e"> {{ e }}</template>
      </Message>

      <section class="panel" aria-labelledby="f-item-title">
        <div id="f-item-title" class="panel-header">The item</div>
        <div class="panel-body form-grid">
          <FormRow label="Type" for="lf-type" :error="errors.type">
            <SelectButton
              id="lf-type"
              :model-value="type"
              :options="typeOptions"
              option-label="label"
              option-value="value"
              :allow-empty="false"
              @update:model-value="(v: LicenseType) => setType(v)"
            />
          </FormRow>
          <FormRow
            label="Template"
            for="lf-template"
            :error="errors.templateId"
            :hint="
              editing
                ? 'Its end of life can be followed; the fields stay the item’s own.'
                : 'Fills in its product and defaults.'
            "
          >
            <Select
              :model-value="d.templateId ?? null"
              input-id="lf-template"
              :options="templateOptions"
              option-label="label"
              option-value="value"
              placeholder="none"
              :loading="templates.isLoading.value"
              filter
              show-clear
              fluid
              @update:model-value="(v: string | null) => setTemplate(v)"
            />
          </FormRow>
          <FormRow
            label="Name"
            for="lf-name"
            :error="errors.name"
            required
            :hint="`What it is called here; names needn't be unique: the number tells them apart.`"
          >
            <InputText
              id="lf-name"
              v-model="d.name"
              :placeholder="pickedTemplate ? templateName(pickedTemplate) : 'Microsoft 365 Business Standard'"
              :invalid="!!errors.name"
              data-test="lf-name"
            />
          </FormRow>
          <FormRow label="Category" for="lf-category" :error="errors.categoryId">
            <Select
              v-model="d.categoryId"
              input-id="lf-category"
              :options="categoryOptions"
              option-label="label"
              option-value="value"
              :loading="categories.isLoading.value"
              :invalid="!!errors.categoryId"
              fluid
            />
          </FormRow>
        </div>
      </section>

      <section class="panel" aria-labelledby="f-product-title">
        <div id="f-product-title" class="panel-header">{{ titleOfGroup }}</div>
        <div class="panel-body form-grid">
          <FormRow label="Manufacturer" for="lf-manufacturer" :error="errors.manufacturer">
            <NameField
              v-model="d.manufacturer"
              field="manufacturers"
              input-id="lf-manufacturer"
              :invalid="!!errors.manufacturer"
            />
          </FormRow>
          <FormRow :label="type === 'Service' ? 'Service' : 'Product'" for="lf-product" :error="errors.productName">
            <InputText id="lf-product" v-model="d.productName" :invalid="!!errors.productName" />
          </FormRow>
          <FormRow :label="type === 'Hardware' ? 'Model' : 'Edition'" for="lf-edition" :error="errors.edition">
            <InputText id="lf-edition" v-model="d.edition" :invalid="!!errors.edition" />
          </FormRow>
          <FormRow :label="vendorLabel(type)" for="lf-vendor" :error="errors.vendor">
            <NameField v-model="d.vendor" field="vendors" input-id="lf-vendor" :invalid="!!errors.vendor" />
          </FormRow>
          <FormRow label="Kind" for="lf-kind" :error="errors.kind" hint="Another kind fills in its defaults below.">
            <Select
              :model-value="kind"
              input-id="lf-kind"
              :options="kindOptions"
              option-label="label"
              option-value="value"
              :invalid="!!errors.kind"
              fluid
              data-test="lf-kind"
              @update:model-value="(v: LicenseKind) => setKind(v)"
            />
          </FormRow>
          <FormRow label="Licensed to" for="lf-licensed-to" :error="errors.licensedTo">
            <InputText id="lf-licensed-to" v-model="d.licensedTo" :invalid="!!errors.licensedTo" />
          </FormRow>
          <FormRow
            :label="type === 'Hardware' ? 'Serial number' : 'Reference'"
            for="lf-reference"
            :error="errors.reference"
          >
            <InputText
              id="lf-reference"
              v-model="d.reference"
              :placeholder="type === 'Hardware' ? '' : 'The order or contract number'"
              :invalid="!!errors.reference"
            />
          </FormRow>
          <FormRow label="Customer number" for="lf-customer" :error="errors.customerNumber">
            <InputText id="lf-customer" v-model="d.customerNumber" :invalid="!!errors.customerNumber" />
          </FormRow>
          <FormRow
            label="Portal"
            for="lf-portal"
            :error="errors.portalUrl"
            wide
            hint="Where it is managed: the vendor's portal, the admin centre."
          >
            <InputText id="lf-portal" v-model="d.portalUrl" placeholder="https://…" :invalid="!!errors.portalUrl" />
          </FormRow>
        </div>
      </section>

      <section class="panel" aria-labelledby="f-support-title">
        <div class="panel-header">
          <span id="f-support-title">Support</span>
          <span class="spacer" />
          <Button v-if="!showSupport" label="Add support details" size="small" text @click="showSupport = true" />
        </div>
        <div v-if="showSupport" class="panel-body form-grid">
          <FormRow
            label="Support contact"
            for="lf-support"
            :error="errors.supportContact"
            wide
            hint="Phone, e-mail, hours. Never passwords."
          >
            <Textarea
              id="lf-support"
              v-model="d.supportContact"
              rows="3"
              auto-resize
              :invalid="!!errors.supportContact"
            />
          </FormRow>
          <FormRow label="Support URL" for="lf-support-url" :error="errors.supportUrl">
            <InputText
              id="lf-support-url"
              v-model="d.supportUrl"
              placeholder="https://…"
              :invalid="!!errors.supportUrl"
            />
          </FormRow>
          <FormRow label="SLA" for="lf-sla" :error="errors.sla">
            <InputText id="lf-sla" v-model="d.sla" placeholder="24/7, 4 h response" :invalid="!!errors.sla" />
          </FormRow>
        </div>
      </section>

      <section class="panel" aria-labelledby="f-where-title">
        <div id="f-where-title" class="panel-header">Where and whose</div>
        <div class="panel-body form-grid">
          <FormRow label="Cost centre" for="lf-cost-center" :error="errors.costCenter">
            <NameField
              v-model="d.costCenter"
              field="costCenters"
              input-id="lf-cost-center"
              :invalid="!!errors.costCenter"
            />
          </FormRow>
          <FormRow
            label="Location"
            for="lf-location"
            :error="errors.locationId"
            hint="Where it is used, as an internet line's site."
          >
            <LocationPicker v-model="d.locationId" input-id="lf-location" :invalid="!!errors.locationId" />
          </FormRow>
          <FormRow label="Owner" for="lf-owner" :error="errors.ownerId" hint="Told by e-mail when it needs attention.">
            <OwnerPicker
              v-model="d.ownerId"
              input-id="lf-owner"
              :current="current?.ownerId ? { id: current.ownerId, name: current.ownerName } : null"
              :invalid="!!errors.ownerId"
            />
          </FormRow>
        </div>
      </section>

      <section class="panel" aria-labelledby="f-keys-title">
        <div id="f-keys-title" class="panel-header">Keys</div>
        <div class="panel-body form-grid">
          <FormRow
            label="Licence key"
            for="lf-license-key"
            :error="errors.licenseKey"
            :hint="editing && current?.hasLicenseKey ? 'Set; empty keeps it.' : 'Encrypted, shown only on request.'"
          >
            <InputText
              id="lf-license-key"
              v-model="d.licenseKey"
              type="password"
              autocomplete="off"
              :invalid="!!errors.licenseKey"
            />
          </FormRow>
          <FormRow
            label="Product key"
            for="lf-product-key"
            :error="errors.productKey"
            :hint="editing && current?.hasProductKey ? 'Set; empty keeps it.' : 'Encrypted, shown only on request.'"
          >
            <InputText
              id="lf-product-key"
              v-model="d.productKey"
              type="password"
              autocomplete="off"
              :invalid="!!errors.productKey"
            />
          </FormRow>
        </div>
      </section>

      <section class="panel" aria-labelledby="f-seats-title">
        <div id="f-seats-title" class="panel-header">Seats</div>
        <div class="panel-body form-grid">
          <FormRow
            label="Seats"
            for="lf-seats"
            :error="errors.seats"
            hint="Empty: not counted (a site licence, a single line)."
          >
            <InputNumber
              v-model="d.seats"
              input-id="lf-seats"
              :min="1"
              :max="1000000"
              :invalid="!!errors.seats"
              fluid
            />
          </FormRow>
          <FormRow label="Unit" for="lf-seat-unit" :error="errors.seatUnit">
            <NameField
              :model-value="d.seatUnit"
              field="seatUnits"
              input-id="lf-seat-unit"
              placeholder="devices"
              :invalid="!!errors.seatUnit"
              @update:model-value="setUnit"
            />
          </FormRow>
          <label class="check wide">
            <Checkbox
              v-model="d.seatsFromHosts"
              binary
              input-id="lf-from-hosts"
              :disabled="d.seats === null || d.seats === undefined"
              @update:model-value="seatsTouched = true"
            />
            <span
              >Counted on the hosts: each host uses its seats, and more than {{ d.seats ?? 'the seats' }} is a finding
              (449)</span
            >
          </label>
        </div>
      </section>

      <section class="panel" aria-labelledby="f-price-title">
        <div id="f-price-title" class="panel-header">Price and billing</div>
        <div class="panel-body form-grid">
          <FormRow label="Amount" for="lf-amount" :error="errors.priceAmount" hint="Empty: unknown or free.">
            <InputNumber
              v-model="d.priceAmount"
              input-id="lf-amount"
              :min="0"
              :min-fraction-digits="2"
              :max-fraction-digits="2"
              :invalid="!!errors.priceAmount"
              fluid
            />
          </FormRow>
          <FormRow
            label="Currency"
            for="lf-currency"
            :error="errors.currency"
            hint="Three letters; empty: PNMT's default."
          >
            <InputText
              id="lf-currency"
              v-model="d.currency"
              maxlength="3"
              placeholder="EUR"
              :invalid="!!errors.currency"
            />
          </FormRow>
          <FormRow label="The amount is" for="lf-basis" :error="errors.priceBasis">
            <SelectButton
              id="lf-basis"
              v-model="d.priceBasis"
              :options="basisOptions"
              option-label="label"
              option-value="value"
              :allow-empty="false"
            />
          </FormRow>
          <FormRow label="Billing" for="lf-billing" :error="errors.billing">
            <Select
              v-model="d.billing"
              input-id="lf-billing"
              :options="billingOptions"
              option-label="label"
              option-value="value"
              fluid
            />
          </FormRow>
          <FormRow
            v-if="d.billing !== 'OneTime'"
            label="Billed every (months)"
            for="lf-billing-months"
            :error="errors.billingMonths"
            :hint="
              d.billing === 'Variable'
                ? 'The amount is an estimate per period; the invoices tell the rest.'
                : 'The amount is per billing period.'
            "
          >
            <InputNumber
              v-model="d.billingMonths"
              input-id="lf-billing-months"
              :min="1"
              :max="120"
              :placeholder="d.termMonths ? String(d.termMonths) : ''"
              :invalid="!!errors.billingMonths"
              fluid
            />
          </FormRow>
          <label class="check wide">
            <Checkbox v-model="d.recordCharges" binary input-id="lf-record" />
            <span>PNMT records the charges: each billing period, or the purchase, as it comes</span>
          </label>
          <label v-if="past > 0" class="check wide" data-test="lf-past-charges">
            <Checkbox v-model="d.recordPastCharges" binary input-id="lf-past" />
            <span
              >Also record the charges since {{ formatDay(d.startsOn ?? today) }} at this price ({{ past }}
              {{ past === 1 ? 'charge' : 'charges' }})</span
            >
          </label>
        </div>
      </section>

      <section class="panel" aria-labelledby="f-term-title">
        <div id="f-term-title" class="panel-header">Term</div>
        <div class="panel-body form-grid">
          <FormRow
            label="Started on"
            for="lf-starts"
            :error="errors.startsOn"
            hint="When it was bought, or the contract began."
          >
            <DayField v-model="d.startsOn" input-id="lf-starts" :invalid="!!errors.startsOn" />
          </FormRow>
          <FormRow
            :label="expiryLabel(kind)"
            for="lf-expires"
            :error="errors.expiresOn"
            :required="needsExpiry(d)"
            :hint="needsExpiry(d) ? undefined : 'Empty: it doesn’t expire.'"
          >
            <DayField v-model="d.expiresOn" input-id="lf-expires" clearable :invalid="!!errors.expiresOn" />
          </FormRow>
          <FormRow label="Term (months)" for="lf-term" :error="errors.termMonths" hint="How often it is renewed.">
            <InputNumber
              v-model="d.termMonths"
              input-id="lf-term"
              :min="1"
              :max="120"
              :invalid="!!errors.termMonths"
              fluid
            />
          </FormRow>
          <FormRow label="Renewal" for="lf-renewal" :error="errors.renewal">
            <Select
              v-model="d.renewal"
              input-id="lf-renewal"
              :options="renewalOptions"
              option-label="label"
              option-value="value"
              fluid
            />
          </FormRow>
          <FormRow
            label="Warn (days)"
            for="lf-warn"
            :error="errors.warnDays"
            :hint="
              d.renewal === 'Automatic'
                ? 'Before the cancellation deadline, if warned.'
                : 'Before it expires; 0: only once expired.'
            "
          >
            <InputNumber
              v-model="d.warnDays"
              input-id="lf-warn"
              :min="0"
              :max="730"
              placeholder="30"
              :invalid="!!errors.warnDays"
              fluid
            />
          </FormRow>
          <template v-if="d.renewal === 'Automatic'">
            <FormRow
              label="Notice (days)"
              for="lf-notice"
              :error="errors.noticeDays"
              :hint="
                d.noticeDays && d.expiresOn
                  ? `Cancel by ${formatDay(addDays(d.expiresOn, -d.noticeDays))}.`
                  : 'How many days before the expiry it must be cancelled.'
              "
            >
              <InputNumber
                v-model="d.noticeDays"
                input-id="lf-notice"
                :min="0"
                :max="730"
                :invalid="!!errors.noticeDays"
                fluid
              />
            </FormRow>
            <label class="check">
              <Checkbox v-model="d.warnBeforeRenewal" binary input-id="lf-warn-renewal" />
              <span>Warn before it renews: the warning days before the cancellation deadline</span>
            </label>
          </template>
        </div>
      </section>

      <section class="panel" aria-labelledby="f-eol-title">
        <div id="f-eol-title" class="panel-header">End of life</div>
        <div class="panel-body form-grid">
          <label v-if="d.templateId" class="check wide">
            <Checkbox v-model="d.endOfLifeFromTemplate" binary input-id="lf-eol-template" />
            <span
              >From the template<template v-if="pickedTemplate?.endOfLifeOn">
                ({{ formatDay(pickedTemplate.endOfLifeOn)
                }}<template v-if="pickedTemplate.endOfLifeNote">, {{ pickedTemplate.endOfLifeNote }}</template
                >)</template
              ><template v-else-if="pickedTemplate"> (it sets none yet)</template></span
            >
          </label>
          <template v-if="!d.templateId || !d.endOfLifeFromTemplate">
            <FormRow label="End of life" for="lf-eol" :error="errors.endOfLifeOn">
              <DayField v-model="d.endOfLifeOn" input-id="lf-eol" clearable :invalid="!!errors.endOfLifeOn" />
            </FormRow>
            <FormRow label="What it is" for="lf-eol-note" :error="errors.endOfLifeNote">
              <InputText
                id="lf-eol-note"
                v-model="d.endOfLifeNote"
                placeholder="end of extended support"
                :invalid="!!errors.endOfLifeNote"
              />
            </FormRow>
          </template>
          <FormRow label="Warn (days)" for="lf-eol-warn" :error="errors.eolWarnDays" hint="Before the end of life.">
            <InputNumber
              v-model="d.eolWarnDays"
              input-id="lf-eol-warn"
              :min="0"
              :max="1095"
              placeholder="90"
              :invalid="!!errors.eolWarnDays"
              fluid
            />
          </FormRow>
        </div>
      </section>

      <section class="panel" aria-labelledby="f-review-title">
        <div id="f-review-title" class="panel-header">Review</div>
        <div class="panel-body form-grid">
          <FormRow
            label="Last reviewed on"
            for="lf-reviewed"
            :error="errors.reviewedOn"
            hint="For a review made before PNMT knew it."
          >
            <DayField v-model="d.reviewedOn" input-id="lf-reviewed" clearable :invalid="!!errors.reviewedOn" />
          </FormRow>
          <FormRow label="Review every (months)" for="lf-review" :error="errors.reviewMonths" hint="Empty: no rhythm.">
            <InputNumber
              v-model="d.reviewMonths"
              input-id="lf-review"
              :min="1"
              :max="60"
              :invalid="!!errors.reviewMonths"
              fluid
            />
          </FormRow>
        </div>
      </section>

      <section class="panel" aria-labelledby="f-note-title">
        <div id="f-note-title" class="panel-header">Note</div>
        <div class="panel-body">
          <FormRow
            label="Note (Markdown)"
            for="lf-note"
            :error="errors.note"
            hint="Keys belong in the key fields above."
          >
            <Textarea id="lf-note" v-model="d.note" rows="6" auto-resize class="mono" :invalid="!!errors.note" />
          </FormRow>
        </div>
      </section>

      <div class="form-actions">
        <Button
          type="submit"
          :label="editing ? 'Save' : 'Create'"
          icon="pi pi-check"
          :loading="saving"
          data-test="lf-save"
        />
        <Button label="Cancel" severity="secondary" text :disabled="saving" @click="cancel" />
      </div>
    </form>
  </div>
</template>

<style scoped>
.licence-form {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  max-width: 64rem;
}

.spacer {
  flex: 1;
}

.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.wide {
  grid-column: 1 / -1;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6, 2rem);
}
</style>
