<script setup lang="ts">
import { computed, ref } from 'vue'
import { money, type LicenseBilling, type LicenseDetail, type LicensePriceBasis } from '@/api/licenses'
import InlineField from '@/components/InlineField.vue'
import { formatDay } from '@/format'
import NameField from '../NameField.vue'
import { billingText } from './item'
import { useItemSave } from './useItemSave'

/**
 * The item's *Seats* and *Price and billing* panels (docs/licences-plan.md §3.3, §6.1, §8.7): the seats and their unit, counted on the
 * hosts or not, and the used ones with ⚠ when over; the amount and currency, in total or per seat, one-time, every n months or variable,
 * the running cost per month and per year, and whether PNMT records the charges (§6.2).
 */
const props = defineProps<{ item: LicenseDetail; readonly: boolean }>()
const { save } = useItemSave(() => props.item)
const number = (v: unknown) => (v === null || v === undefined || v === '' ? null : Number(v))

const basisOptions: { label: string; value: LicensePriceBasis }[] = [
  { label: 'In total', value: 'Total' },
  { label: 'Per seat', value: 'PerSeat' },
]
const billingOptions: { label: string; value: LicenseBilling }[] = [
  { label: 'One-time', value: 'OneTime' },
  { label: 'Recurring', value: 'Recurring' },
  { label: 'Variable (paid by use)', value: 'Variable' },
]
const over = computed(() =>
  props.item.seatsFromHosts && props.item.seats !== null ? props.item.usedSeats - props.item.seats : 0,
)
const unitDraft = ref<string | null | undefined>(null)
</script>

<template>
  <section class="panel" aria-labelledby="seats-title">
    <div id="seats-title" class="panel-header">Seats</div>
    <div class="panel-body">
      <dl class="facts">
        <dt>Seats</dt>
        <dd>
          <InlineField
            :value="item.seats"
            type="number"
            label="Seats"
            field="seats"
            placeholder="not counted"
            :readonly="readonly"
            :save="(v) => save({ seats: number(v), seatsFromHosts: number(v) === null ? false : item.seatsFromHosts })"
          />
        </dd>
        <dt>Unit</dt>
        <dd>
          <InlineField
            :value="item.seatUnit"
            type="custom"
            label="Unit"
            field="seatUnit"
            :readonly="readonly"
            :save="() => save({ seatUnit: unitDraft?.trim() || 'devices' })"
            @edit="unitDraft = item.seatUnit"
          >
            <template #editor>
              <NameField v-model="unitDraft" field="seatUnits" input-id="li-seat-unit" />
            </template>
          </InlineField>
        </dd>
        <dt>Counted on hosts</dt>
        <dd>
          <InlineField
            :value="item.seatsFromHosts"
            type="bool"
            label="Counted on hosts"
            field="seats"
            :readonly="readonly || item.seats === null"
            :save="(v) => save({ seatsFromHosts: Boolean(v) })"
          />
          <small v-if="item.seats === null" class="field-hint">needs the seats</small>
        </dd>
        <template v-if="item.seatsFromHosts && item.seats !== null">
          <dt>Used</dt>
          <dd :class="{ over: over > 0 }" data-test="seats-used">
            <i v-if="over > 0" class="pi pi-exclamation-triangle" aria-hidden="true" />
            {{ item.usedSeats }} of {{ item.seats }} {{ item.seatUnit
            }}<template v-if="over > 0">, over by {{ over }}</template>
          </dd>
        </template>
      </dl>
    </div>
  </section>

  <section class="panel" aria-labelledby="price-title">
    <div id="price-title" class="panel-header">Price and billing</div>
    <div class="panel-body">
      <dl class="facts">
        <dt>Amount</dt>
        <dd>
          <InlineField
            :value="item.priceAmount"
            type="number"
            label="Amount"
            field="priceAmount"
            placeholder="unknown"
            :readonly="readonly"
            :save="(v) => save({ priceAmount: number(v) })"
          />
        </dd>
        <dt>Currency</dt>
        <dd>
          <InlineField
            :value="item.currency"
            label="Currency"
            field="currency"
            mono
            :readonly="readonly"
            :save="(v) => save({ currency: v ? String(v).trim().toUpperCase() : null })"
          />
        </dd>
        <dt>The amount is</dt>
        <dd>
          <InlineField
            :value="item.priceBasis"
            type="enum"
            :options="basisOptions"
            label="The amount is"
            field="priceBasis"
            :readonly="readonly"
            :save="(v) => save({ priceBasis: v as LicensePriceBasis })"
          />
        </dd>
        <dt>Billing</dt>
        <dd>
          <InlineField
            :value="item.billing"
            type="enum"
            :options="billingOptions"
            label="Billing"
            field="billing"
            :readonly="readonly"
            :save="(v) => save({ billing: v as LicenseBilling })"
          />
        </dd>
        <template v-if="item.billing !== 'OneTime'">
          <dt>Billed every</dt>
          <dd>
            <InlineField
              :value="item.billingMonths"
              type="number"
              label="Billed every"
              field="billingMonths"
              :readonly="readonly"
              :save="(v) => save({ billingMonths: number(v) })"
            />
            <small class="field-hint">months</small>
          </dd>
        </template>
        <dt>Costs</dt>
        <dd data-test="running-cost">
          <template v-if="item.billing === 'OneTime'">
            {{ item.unitPrice === null ? 'no price' : `${money(item.unitPrice, item.currency)} once` }}
          </template>
          <template v-else>
            {{ money(item.perMonth, item.currency) }} per month · {{ money(item.perYear, item.currency) }} per year
            <span class="muted">({{ billingText(item.billing, item.billingMonths) }})</span>
          </template>
          <div v-if="item.priceBasis === 'PerSeat' && item.unitPrice !== null" class="muted small">
            {{ money(item.priceAmount, item.currency) }} × {{ item.seats }} seats =
            {{ money(item.unitPrice, item.currency) }}
          </div>
        </dd>
        <dt>PNMT records the charges</dt>
        <dd>
          <InlineField
            :value="item.recordCharges"
            type="bool"
            label="PNMT records the charges"
            field="recordCharges"
            :readonly="readonly"
            :save="(v) => save({ recordCharges: Boolean(v) })"
          />
          <small v-if="item.recordCharges && item.chargesFrom" class="field-hint"
            >since {{ formatDay(item.chargesFrom) }}</small
          >
          <small v-else-if="!item.recordCharges" class="field-hint">record them by hand on the Costs tab</small>
        </dd>
      </dl>
    </div>
  </section>
</template>

<style scoped>
.over {
  color: var(--pnmt-warn);
}

.small {
  font-size: 0.857rem;
}

.field-hint {
  margin-left: var(--pnmt-space-2);
}
</style>
