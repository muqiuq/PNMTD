<script setup lang="ts" generic="T extends BarDatum & { items?: number }">
import Button from 'primevue/button'
import { computed, ref } from 'vue'
import { RouterLink, type RouteLocationRaw } from 'vue-router'
import { money } from '@/api/licenses'
import { rankedLayout, type BarDatum } from './chartScale'

/**
 * Ranked horizontal bars (docs/licences-plan.md §8.2, §8.12): one bar per category or provider, largest first, in one hue (the
 * categories get no colours of their own: the length tells them apart), the value printed at each bar's end; names and values
 * in the text tokens. `pinned` keeps a row last (*Other providers (14)*). Each row is a stop for Tab with its tooltip on hover
 * and on focus; with `link`, a click opens what it sums. *Show as table* gives the same figures.
 */
const props = withDefaults(
  defineProps<{
    rows: readonly T[]
    currency: string
    /** Says what is plotted, with the currency: "Per year by category · EUR". */
    title: string
    /** The table's first column: Category, Provider. */
    nameHeader?: string
    /** After an amount in the tooltip and the table: "a year". */
    per?: string
    pinned?: (row: T) => boolean
    link?: (row: T) => RouteLocationRaw | undefined
    /** What a click does, for the accessible name: "Open its costs". */
    linkHint?: string
    emptyText?: string
  }>(),
  {
    nameHeader: 'Name',
    per: '',
    pinned: undefined,
    link: undefined,
    linkHint: '',
    emptyText: 'Nothing to show.',
  },
)

const bars = computed(() => rankedLayout(props.rows, props.pinned))
const total = computed(() => props.rows.reduce((n, r) => n + r.amount, 0))
const table = ref(false)
const active = ref<number | null>(null)

const whole = new Intl.NumberFormat('en-GB', { maximumFractionDigits: 0 })
const m = (amount: number) => `${money(amount, props.currency)}${props.per ? ` ${props.per}` : ''}`
const share = (amount: number) => (total.value > 0 ? `${Math.round((amount / total.value) * 100)}% of the total` : '')
const itemsText = (n: number | undefined) => (n === undefined ? '' : n === 1 ? '1 item' : `${n} items`)

function describe(bar: T): string {
  const parts = [`${bar.label}: ${m(bar.amount)}`, itemsText(bar.items), share(bar.amount)].filter(Boolean)
  const target = props.link?.(bar)
  return `${parts.join(', ')}${target && props.linkHint ? `. ${props.linkHint}` : ''}`
}
</script>

<template>
  <figure class="ranked-bars" data-test="ranked-bars">
    <figcaption class="head">
      <span class="title">{{ title }}</span>
      <Button
        v-if="rows.length"
        :label="table ? 'Show as chart' : 'Show as table'"
        :icon="table ? 'pi pi-chart-bar' : 'pi pi-table'"
        size="small"
        text
        :aria-pressed="table"
        @click="table = !table"
      />
    </figcaption>
    <p v-if="!rows.length" class="muted empty">{{ emptyText }}</p>
    <ul v-else-if="!table" class="bars">
      <li v-for="(bar, i) in bars" :key="`${i}:${bar.key}`" class="bar-row" :class="{ active: active === i }">
        <component
          :is="link?.(bar) ? RouterLink : 'div'"
          :to="link?.(bar)"
          class="hit"
          :tabindex="link?.(bar) ? undefined : 0"
          :aria-label="describe(bar)"
          data-test="ranked-bar"
          @pointerenter="active = i"
          @pointerleave="active = null"
          @focus="active = i"
          @blur="active = null"
        >
          <span class="name ellipsis" :title="bar.label">{{ bar.label }}</span>
          <span class="track" aria-hidden="true">
            <span class="bar" :style="{ width: `calc((100% - 5rem) * ${bar.fraction})` }" />
            <span class="value">{{ whole.format(bar.amount) }}</span>
          </span>
        </component>
        <div v-if="active === i" class="tooltip" aria-hidden="true">
          <div class="tip-name">{{ bar.label }}</div>
          <div class="tip-total">{{ m(bar.amount) }}</div>
          <div v-if="bar.items !== undefined || share(bar.amount)" class="muted">
            {{ [itemsText(bar.items), share(bar.amount)].filter(Boolean).join(' · ') }}
          </div>
        </div>
      </li>
    </ul>
    <div v-else class="table-wrap">
      <table class="chart-table" data-test="ranked-table">
        <caption class="sr-only">
          {{
            title
          }}
        </caption>
        <thead>
          <tr>
            <th scope="col">{{ nameHeader }}</th>
            <th scope="col" class="num">{{ per ? `Amount ${per}` : 'Amount' }}</th>
            <th v-if="rows.some((r) => r.items !== undefined)" scope="col" class="num">Items</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(bar, i) in bars" :key="`${i}:${bar.key}`">
            <th scope="row">
              <RouterLink v-if="link?.(bar)" :to="link(bar)!">{{ bar.label }}</RouterLink>
              <template v-else>{{ bar.label }}</template>
            </th>
            <td class="num">{{ money(bar.amount, currency) }}</td>
            <td v-if="rows.some((r) => r.items !== undefined)" class="num">{{ bar.items ?? '' }}</td>
          </tr>
        </tbody>
        <tfoot>
          <tr>
            <th scope="row">Total</th>
            <td class="num">{{ money(total, currency) }}</td>
            <td v-if="rows.some((r) => r.items !== undefined)" class="num">
              {{ rows.reduce((n, r) => n + (r.items ?? 0), 0) }}
            </td>
          </tr>
        </tfoot>
      </table>
    </div>
  </figure>
</template>

<style scoped>
.ranked-bars {
  --bar: var(--pnmt-chart-seq-5);
  margin: 0;
  min-width: 0;
}

.head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pnmt-space-2);
  margin-bottom: var(--pnmt-space-1);
}

.title {
  font-weight: 600;
}

.empty {
  margin: var(--pnmt-space-2) 0;
}

.bars {
  list-style: none;
  margin: 0;
  padding: 0;
}

.bar-row {
  position: relative;
}

.hit {
  display: grid;
  grid-template-columns: minmax(6rem, 38%) minmax(0, 1fr);
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: 3px var(--pnmt-space-1);
  border-radius: var(--p-border-radius-sm);
  color: var(--pnmt-text);
  outline: none;
}

a.hit:hover {
  text-decoration: none;
}

.bar-row.active .hit {
  background: color-mix(in srgb, var(--pnmt-text) 6%, transparent);
}

.hit:focus-visible {
  box-shadow: 0 0 0 2px var(--p-primary-color);
}

.name {
  font-size: 0.929rem;
}

.track {
  display: flex;
  align-items: center;
  min-width: 0;
}

.bar {
  display: block;
  height: 12px;
  /* The data end rounded, square at the baseline. */
  border-radius: 0 4px 4px 0;
  background: var(--bar);
  flex: none;
}

.value {
  margin-left: 6px;
  color: var(--pnmt-text);
  font-size: 0.857rem;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.tooltip {
  position: absolute;
  z-index: 2;
  left: 38%;
  top: calc(100% + 2px);
  min-width: 10rem;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  background: var(--pnmt-panel);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  box-shadow: 0 4px 12px rgb(0 0 0 / 0.15);
  font-size: 0.857rem;
  pointer-events: none;
}

.tip-name {
  color: var(--pnmt-muted);
}

.tip-total {
  font-size: 1rem;
  font-weight: 600;
}

.table-wrap {
  overflow-x: auto;
}

.chart-table {
  border-collapse: collapse;
  width: 100%;
  font-size: 0.929rem;
}

.chart-table th,
.chart-table td {
  padding: 3px var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
  text-align: left;
}

.chart-table thead th {
  color: var(--pnmt-muted);
  font-weight: 500;
}

.chart-table tbody th {
  font-weight: 400;
}

.chart-table .num {
  text-align: right;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.chart-table tfoot th,
.chart-table tfoot td {
  font-weight: 600;
  border-bottom: none;
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
