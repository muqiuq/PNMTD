<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  chargeKindLabels,
  licenseKeys,
  licensesApi,
  money,
  type ChargesQuery,
  type LicenseCharge,
  type LicenseChargeKind,
  type MoneyFilter,
} from '@/api/licenses'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import { formatAbsolute, formatDay } from '@/format'
import { useSession } from '@/stores/session'
import ChargeDialog from './dialogs/ChargeDialog.vue'
import DayField from './dialogs/DayField.vue'
import MoneyFilters from './MoneyFilters.vue'
import { readCharges, writeCharges } from './moneyFilter'

/**
 * The Costs tab's *Charges* view (docs/licences-plan.md §8.5, `?show=charges`): every charge, PNMT's and people's, with its day,
 * item, kind, period, amount (credits negative), whether it is an estimate, who recorded it, its reference and its invoice
 * (with Details). Filtered by period, type, category, item, kind, source and *estimated*, kept in the URL. With Edit, a charge
 * is corrected in ChargeDialog (§6.2, §6.3): this is where invoices are checked and estimates confirmed. CSV with the same
 * filters. The slot takes the tab's own buttons (the view switch, *Record a cost…*).
 */
const props = defineProps<{ edit: boolean }>()
const route = useRoute()
const router = useRouter()
const session = useSession()
const details = computed(() => session.can('Licenses', 'Details'))

const query = computed(() => readCharges(route.query))
function update(patch: Partial<ChargesQuery>) {
  void router.replace({ query: writeCharges(route.query, { ...query.value, ...patch }) })
}
const shared = computed<MoneyFilter>({
  get: () => ({ type: query.value.type, category: query.value.category }),
  set: (f) => update({ type: f.type, category: f.category }),
})

const charges = useQuery({
  queryKey: computed(() => licenseKeys.charges(query.value)),
  queryFn: ({ queryKey }) => licensesApi.charges(queryKey[2]),
  placeholderData: keepPreviousData,
})
const items = useQuery({ queryKey: licenseKeys.list(), queryFn: () => licensesApi.list(), staleTime: 30_000 })
const itemOptions = computed(() =>
  (items.data.value ?? []).map((i) => ({ value: i.id, label: `${i.display} ${i.name}` })),
)
const kindOptions = (Object.keys(chargeKindLabels) as LicenseChargeKind[]).map((k) => ({
  value: k,
  label: chargeKindLabels[k],
}))
const sourceOptions = [
  { value: 'Automatic', label: 'Recorded by PNMT' },
  { value: 'Person', label: 'Recorded by hand' },
]
const estimatedOptions = [
  { value: true, label: 'Estimates' },
  { value: false, label: 'Not estimates' },
]

const rows = computed(() => charges.data.value ?? [])
const filtered = computed(() => Object.keys(query.value).length > 0)
const stale = computed(() => charges.isFetching.value && charges.isPlaceholderData.value)
/** The sums per currency, never added across (§6.6). */
const sums = computed(() => {
  const by = new Map<string, number>()
  for (const c of rows.value) by.set(c.currency, (by.get(c.currency) ?? 0) + c.amount)
  return [...by.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([currency, amount]) => money(amount, currency))
})

const period = (c: LicenseCharge) =>
  c.periodFrom || c.periodTo
    ? `${c.periodFrom ? formatDay(c.periodFrom) : '…'} – ${c.periodTo ? formatDay(c.periodTo) : '…'}`
    : ''
const source = (c: LicenseCharge) => (c.source === 'Automatic' ? 'PNMT' : (c.createdBy ?? 'By hand'))

// Correcting a charge (Edit).
const editing = ref<LicenseCharge | null>(null)
const dialog = ref(false)
function correct(c: LicenseCharge) {
  editing.value = c
  dialog.value = true
}
</script>

<template>
  <div class="charges-view" data-test="charges-view">
    <div class="filters" role="group" aria-label="Filter the charges">
      <div class="day">
        <label for="charges-from" class="sr-only">From</label>
        <DayField
          :model-value="query.from ?? null"
          input-id="charges-from"
          placeholder="From"
          clearable
          @update:model-value="(v) => update({ from: v ?? undefined })"
        />
      </div>
      <div class="day">
        <label for="charges-to" class="sr-only">To</label>
        <DayField
          :model-value="query.to ?? null"
          input-id="charges-to"
          placeholder="To"
          clearable
          @update:model-value="(v) => update({ to: v ?? undefined })"
        />
      </div>
      <MoneyFilters v-model="shared" :fields="['type', 'category']" />
      <Select
        :model-value="query.license ?? null"
        :options="itemOptions"
        option-label="label"
        option-value="value"
        placeholder="All items"
        aria-label="Item"
        show-clear
        filter
        :loading="items.isPending.value"
        size="small"
        class="item"
        data-test="charges-item"
        @update:model-value="(v: string | null) => update({ license: v ?? undefined })"
      />
      <Select
        :model-value="query.kind ?? null"
        :options="kindOptions"
        option-label="label"
        option-value="value"
        placeholder="All kinds"
        aria-label="Kind"
        show-clear
        size="small"
        @update:model-value="(v: LicenseChargeKind | null) => update({ kind: v ?? undefined })"
      />
      <Select
        :model-value="query.source ?? null"
        :options="sourceOptions"
        option-label="label"
        option-value="value"
        placeholder="Anyone"
        aria-label="Recorded by"
        show-clear
        size="small"
        @update:model-value="(v: 'Automatic' | 'Person' | null) => update({ source: v ?? undefined })"
      />
      <Select
        :model-value="query.estimated ?? null"
        :options="estimatedOptions"
        option-label="label"
        option-value="value"
        placeholder="Estimated or not"
        aria-label="Estimated"
        show-clear
        size="small"
        data-test="charges-estimated"
        @update:model-value="(v: boolean | null) => update({ estimated: v ?? undefined })"
      />
      <div class="actions">
        <Button
          as="a"
          :href="licensesApi.chargesExportUrl(query)"
          download
          label="CSV"
          icon="pi pi-download"
          size="small"
          text
          data-test="charges-csv"
        />
        <slot />
      </div>
    </div>

    <ErrorState
      v-if="charges.isError.value"
      :error="charges.error.value"
      what="the charges"
      @retry="charges.refetch()"
    />
    <div v-else class="panel" :class="{ stale }">
      <DataTable
        :value="rows"
        :loading="charges.isLoading.value"
        size="small"
        data-key="id"
        sort-field="on"
        :sort-order="-1"
        removable-sort
        paginator
        :rows="50"
        :always-show-paginator="false"
        data-test="charges-table"
      >
        <Column header="Day" field="on" sortable style="width: 8rem">
          <template #body="{ data }">
            <span class="nowrap" :title="`Recorded ${formatAbsolute(data.createdAt)}`">{{ formatDay(data.on) }}</span>
          </template>
        </Column>
        <Column header="Item" field="number" sortable>
          <template #body="{ data }">
            <RouterLink :to="{ name: 'licence', params: { number: String(data.number) } }"
              ><span class="muted">{{ data.display }}</span> {{ data.name }}</RouterLink
            >
          </template>
        </Column>
        <Column header="Kind" field="kind" sortable>
          <template #body="{ data }">{{ chargeKindLabels[data.kind as LicenseChargeKind] }}</template>
        </Column>
        <Column header="Period">
          <template #body="{ data }"
            ><span class="nowrap muted">{{ period(data) }}</span></template
          >
        </Column>
        <Column header="Amount" field="amount" sortable body-class="num" header-class="num">
          <template #body="{ data }"
            ><span class="nowrap" :class="{ credit: data.amount < 0 }">{{
              money(data.amount, data.currency)
            }}</span></template
          >
        </Column>
        <Column header="Estimated" field="estimated" sortable>
          <template #body="{ data }">
            <Tag v-if="data.estimated" value="Estimate" severity="secondary" icon="pi pi-question-circle" />
          </template>
        </Column>
        <Column header="Source" field="source" sortable>
          <template #body="{ data }">{{ source(data) }}</template>
        </Column>
        <Column header="Reference" field="reference" sortable>
          <template #body="{ data }"
            ><span class="ellipsis reference" :title="data.note ?? undefined">{{ data.reference }}</span></template
          >
        </Column>
        <Column header="Invoice">
          <template #body="{ data }">
            <a
              v-if="data.invoiceFileId && data.invoiceName && details"
              :href="licensesApi.documentUrl(data.licenseId, data.invoiceFileId, data.invoiceName)"
              target="_blank"
              rel="noopener"
              class="ellipsis invoice"
              ><i class="pi pi-file" aria-hidden="true" /> {{ data.invoiceName }}</a
            >
            <span v-else-if="data.invoiceFileId" class="muted">attached</span>
          </template>
        </Column>
        <Column v-if="props.edit" header="" style="width: 3rem">
          <template #body="{ data }">
            <Button
              icon="pi pi-pencil"
              text
              rounded
              size="small"
              :aria-label="`Correct the charge of ${data.display} on ${formatDay(data.on)}`"
              data-test="charge-correct"
              @click="correct(data)"
            />
          </template>
        </Column>
        <template #empty>
          <EmptyState
            v-if="filtered"
            icon="pi pi-filter"
            title="No charge matches."
            text="Change or clear the filters above."
          />
          <EmptyState
            v-else
            icon="pi pi-receipt"
            title="No charges yet."
            text="PNMT records each billing period of an item that records its charges; extras, credits and purchases are recorded by hand."
          />
        </template>
      </DataTable>
      <p v-if="rows.length" class="sums muted" data-test="charges-sums">
        {{ rows.length === 1 ? '1 charge' : `${rows.length} charges` }} · {{ sums.join(' · ') }}
      </p>
    </div>

    <!-- Mounted before it opens: the dialog fills its fields when it becomes visible. -->
    <ChargeDialog
      v-if="props.edit"
      v-model:visible="dialog"
      :license-id="editing?.licenseId ?? ''"
      :display="editing?.display ?? ''"
      :currency="editing?.currency ?? ''"
      :charge="editing"
    />
  </div>
</template>

<style scoped>
.charges-view {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.filters {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.day {
  width: 10rem;
}

.item {
  min-width: 12rem;
  max-width: 20rem;
}

.actions {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  margin-left: auto;
}

.stale {
  opacity: 0.6;
}

:deep(td.num) {
  text-align: right;
}

:deep(th.num .p-datatable-column-header-content) {
  justify-content: flex-end;
}

.credit {
  font-style: italic;
}

.reference,
.invoice {
  display: inline-block;
  max-width: 12rem;
  vertical-align: bottom;
}

.sums {
  margin: 0;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-top: 1px solid var(--pnmt-border);
  font-size: 0.929rem;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
}
</style>
