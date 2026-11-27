<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import {
  chargeKindLabels,
  licenseKeys,
  licensesApi,
  money,
  todayIso,
  type CostsQuery,
  type LicenseCharge,
  type LicenseDetail,
} from '@/api/licenses'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import { useActions } from '@/composables/useActions'
import { formatDay } from '@/format'
import ChargeDialog from '../dialogs/ChargeDialog.vue'
import { addMonths, billingText, spentByYear } from './item'

/**
 * The item's *Costs* tab (docs/licences-plan.md §6.1–§6.3, §8.7): the running cost per month and per year, what was spent per year,
 * and the charges, newest first: day, kind, period, amount, *estimated*, reference, the invoice and who recorded it. *Record a cost…*
 * and corrections are Edit's; deleting a person's charge is Manage's. PNMT's own can't be deleted: it would be recorded again.
 */
const props = defineProps<{ item: LicenseDetail; edit: boolean; manage: boolean; details: boolean }>()
const { done, ask, notify } = useActions()

const charges = useQuery({
  queryKey: computed(() => licenseKeys.itemCharges(props.item.id)),
  queryFn: () => licensesApi.itemCharges(props.item.id),
})
const rows = computed(() =>
  [...(charges.data.value ?? [])].sort((a, b) => b.on.localeCompare(a.on) || b.createdAt.localeCompare(a.createdAt)),
)
const years = computed(() => spentByYear(rows.value))
// The forecast of the next 12 months (§6.4): the costs view's for this item, from this month on.
const forecastQuery = computed<CostsQuery>(() => {
  const today = todayIso()
  return {
    from: today.slice(0, 7),
    to: addMonths(today, 11).slice(0, 7),
    group: 'item',
    show: 'forecast',
    currency: props.item.currency,
    type: props.item.type,
    category: props.item.categoryId,
  }
})
const forecast = useQuery({
  queryKey: computed(() => licenseKeys.costs(forecastQuery.value)),
  queryFn: () => licensesApi.costs(forecastQuery.value),
  enabled: computed(() => props.item.status !== 'Retired'),
})
const next12 = computed(() => {
  const costs = forecast.data.value
  if (!costs) return null
  const line =
    costs.groups.find((g) => g.itemId === props.item.id) ??
    costs.groups.flatMap((g) => g.items).find((l) => l.itemId === props.item.id)
  return line?.total ?? 0
})
const renewedByHandSoon = computed(
  () => props.item.renewal === 'Manual' && !!props.item.expiresOn && props.item.expiresOn < addMonths(todayIso(), 12),
)
const running = computed(() => {
  const i = props.item
  if (i.billing === 'OneTime') return i.unitPrice === null ? 'No price' : `${money(i.unitPrice, i.currency)} one-time`
  return `${money(i.perMonth, i.currency)} per month · ${money(i.perYear, i.currency)} per year`
})

const dialog = ref(false)
const editing = ref<LicenseCharge | null>(null)
function record() {
  editing.value = null
  dialog.value = true
}
function correct(c: LicenseCharge) {
  editing.value = c
  dialog.value = true
}

const period = (c: LicenseCharge) =>
  c.periodFrom || c.periodTo
    ? `${c.periodFrom ? formatDay(c.periodFrom) : '…'} – ${c.periodTo ? formatDay(c.periodTo) : '…'}`
    : ''
const invoiceUrl = (c: LicenseCharge) =>
  c.invoiceFileId && c.invoiceName ? licensesApi.documentUrl(props.item.id, c.invoiceFileId, c.invoiceName) : null

async function remove(c: LicenseCharge) {
  if (c.source === 'Automatic') {
    notify.info('PNMT’s own charge stays', 'It would be recorded again: set it to 0 with a note instead.')
    return
  }
  if (
    !(await ask(
      `Delete the charge of ${formatDay(c.on)}?`,
      `${money(c.amount, c.currency)} leaves the costs of ${props.item.display}. The audit log keeps it.`,
    ))
  )
    return
  await done(() => licensesApi.deleteCharge(props.item.id, c.id), {
    success: 'Charge deleted',
    failure: 'Not deleted',
    invalidate: [licenseKeys.all],
  })
}
</script>

<template>
  <div class="costs">
    <section class="panel summary" aria-label="Costs">
      <div class="figures">
        <div>
          <div class="muted label">Running cost</div>
          <div class="value" data-test="item-running">{{ running }}</div>
          <div v-if="item.billing !== 'OneTime'" class="muted small">
            {{ billingText(item.billing, item.billingMonths) }}
          </div>
        </div>
        <div v-if="item.status !== 'Retired'">
          <div class="muted label">Next 12 months</div>
          <div class="value" data-test="item-forecast">{{ next12 === null ? '…' : money(next12, item.currency) }}</div>
          <div v-if="renewedByHandSoon" class="muted small">
            after {{ formatDay(item.expiresOn ?? '') }} only if renewed
          </div>
        </div>
        <div>
          <div class="muted label">Spent</div>
          <div v-if="years.length" class="value" data-test="spent-by-year">
            <span v-for="(y, i) in years" :key="y.year"
              >{{ i ? ' · ' : '' }}{{ y.year }}:
              {{ y.amounts.map((m) => money(m.amount, m.currency)).join(' + ') }}</span
            >
          </div>
          <div v-else class="value muted">nothing recorded yet</div>
        </div>
      </div>
      <p v-if="!item.recordCharges" class="muted small">
        PNMT doesn't record this item's charges: record them with Record a cost….
      </p>
      <p v-else-if="item.chargesFrom" class="muted small">
        PNMT records each billing period since {{ formatDay(item.chargesFrom) }}.
      </p>
    </section>

    <section class="panel" aria-labelledby="charges-title">
      <div class="panel-header">
        <span id="charges-title">Charges</span>
        <span class="spacer" />
        <Button
          v-if="edit"
          label="Record a cost…"
          icon="pi pi-plus"
          size="small"
          text
          data-test="record-cost"
          @click="record"
        />
      </div>
      <ErrorState
        v-if="charges.isError.value"
        :error="charges.error.value"
        what="the charges"
        @retry="charges.refetch()"
      />
      <EmptyState v-else-if="charges.data.value && !rows.length" icon="pi pi-wallet" title="No charges yet." />
      <DataTable v-else :value="rows" :loading="charges.isPending.value" data-key="id" size="small" data-test="charges">
        <Column header="Day" style="width: 8rem">
          <template #body="{ data }"
            ><span class="nowrap">{{ formatDay(data.on) }}</span></template
          >
        </Column>
        <Column header="Kind">
          <template #body="{ data }">
            {{ chargeKindLabels[data.kind as LicenseCharge['kind']] }}
            <span v-if="data.source === 'Automatic'" class="muted small">by PNMT</span>
          </template>
        </Column>
        <Column header="Period">
          <template #body="{ data }"
            ><span class="nowrap muted">{{ period(data) }}</span></template
          >
        </Column>
        <Column header="Amount" class="amount">
          <template #body="{ data }">
            <span class="nowrap" :class="{ credit: data.amount < 0 }">{{ money(data.amount, data.currency) }}</span>
            <Tag v-if="data.estimated" value="estimated" severity="warn" class="chip" />
          </template>
        </Column>
        <Column header="Reference">
          <template #body="{ data }">{{ data.reference ?? '' }}</template>
        </Column>
        <Column header="Invoice">
          <template #body="{ data }">
            <a v-if="details && invoiceUrl(data)" :href="invoiceUrl(data) ?? undefined" download
              ><i class="pi pi-file" aria-hidden="true" /> {{ data.invoiceName }}</a
            >
            <span v-else-if="data.invoiceName" class="muted">{{ data.invoiceName }}</span>
          </template>
        </Column>
        <Column header="By">
          <template #body="{ data }">
            <span class="muted">{{ data.source === 'Automatic' ? 'PNMT' : (data.createdBy ?? '–') }}</span>
            <span v-if="data.updatedBy" class="muted small"> · corrected by {{ data.updatedBy }}</span>
          </template>
        </Column>
        <Column v-if="edit" style="width: 5.5rem">
          <template #body="{ data }">
            <span class="actions">
              <Button
                icon="pi pi-pencil"
                text
                rounded
                size="small"
                severity="secondary"
                :aria-label="`Correct the charge of ${formatDay(data.on)}`"
                @click="correct(data)"
              />
              <Button
                v-if="manage"
                icon="pi pi-trash"
                text
                rounded
                size="small"
                severity="secondary"
                :aria-label="`Delete the charge of ${formatDay(data.on)}`"
                @click="remove(data)"
              />
            </span>
          </template>
        </Column>
      </DataTable>
    </section>

    <ChargeDialog
      v-if="edit"
      v-model:visible="dialog"
      :license-id="item.id"
      :display="item.display"
      :currency="item.currency"
      :charge="editing"
    />
  </div>
</template>

<style scoped>
.costs {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.summary {
  padding: var(--pnmt-space-3);
}

.figures {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-6, 2rem);
}

.label {
  font-size: 0.857rem;
}

.value {
  font-size: 1.05rem;
  font-weight: 500;
}

.small {
  font-size: 0.857rem;
}

.summary p {
  margin: var(--pnmt-space-2) 0 0;
}

.spacer {
  flex: 1;
}

.credit {
  color: var(--pnmt-ok);
}

.chip {
  margin-left: var(--pnmt-space-1);
}

.actions {
  display: inline-flex;
}
</style>
