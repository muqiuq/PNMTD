<script setup lang="ts">
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { kindLabels, type LicenseRow } from '@/api/licenses'
import { formatAbsolute, formatDay } from '@/format'
import {
  expiresCell,
  hostsCell,
  periodPrice,
  priceText,
  productText,
  renewalLabels,
  termLines,
  type ColumnKey,
} from './licenceList'

/**
 * One cell of the Licences, Services and Hardware tables (docs/licences-plan.md §8.3), by its column: never the colour alone, a
 * warning has its icon and words too.
 */
const props = defineProps<{ column: ColumnKey; row: LicenseRow; today: string }>()
const expires = computed(() => expiresCell(props.row, props.today))
const hosts = computed(() => hostsCell(props.row))
const pastEol = computed(() => !!props.row.endOfLifeOn && props.row.endOfLifeOn < props.today)
const day = (value: string | null) => (value ? formatDay(value) : '–')
</script>

<template>
  <template v-if="column === 'product'">
    <span v-if="productText(row)">{{ productText(row) }}</span>
    <span v-else class="muted">–</span>
  </template>
  <template v-else-if="column === 'vendor'">{{ row.vendor ?? '–' }}</template>
  <template v-else-if="column === 'hosts'">
    <span :class="{ warn: hosts.over }" :title="hosts.title">
      {{ hosts.text }}
      <i v-if="hosts.over" class="pi pi-exclamation-triangle" aria-hidden="true" />
      <span v-if="hosts.over" class="sr-only">over-assigned</span>
    </span>
  </template>
  <template v-else-if="column === 'kind'">{{ kindLabels[row.kind] }}</template>
  <template v-else-if="column === 'category'">{{ row.categoryName }}</template>
  <template v-else-if="column === 'costCenter'">{{ row.costCenter ?? '–' }}</template>
  <template v-else-if="column === 'location'">{{ row.locationName ?? '–' }}</template>
  <template v-else-if="column === 'owner'">
    <span :class="{ muted: !row.ownerName }">{{ row.ownerName ?? '–' }}</span>
  </template>
  <template v-else-if="column === 'term'">
    <span v-for="(line, i) in termLines(row, today)" :key="i" class="line" :class="{ muted: i > 0 }">{{ line }}</span>
  </template>
  <template v-else-if="column === 'renewal'">{{ renewalLabels[row.renewal] }}</template>
  <template v-else-if="column === 'started'">{{ day(row.startsOn) }}</template>
  <template v-else-if="column === 'expires'">
    <span class="nowrap" :class="expires.tone ? `tone-${expires.tone}` : undefined">
      <template v-if="expires.day">{{ expires.day }} <span class="muted">· </span></template>
      <i v-if="expires.tone === 'danger'" class="pi pi-times-circle" aria-hidden="true" />
      <i v-else-if="expires.tone === 'warn'" class="pi pi-exclamation-triangle" aria-hidden="true" />
      <span :class="{ muted: !expires.tone }">{{ expires.extra }}</span>
    </span>
  </template>
  <template v-else-if="column === 'eol'">
    <span class="nowrap" :class="{ 'tone-danger': pastEol }">
      <i v-if="pastEol" class="pi pi-times-circle" aria-hidden="true" />
      {{ day(row.endOfLifeOn) }}
      <span v-if="pastEol" class="sr-only">(past)</span>
    </span>
  </template>
  <template v-else-if="column === 'seats'">
    <span v-if="row.seats !== null">{{ row.seats }} {{ row.seatUnit }}</span>
    <span v-else class="muted">not counted</span>
  </template>
  <template v-else-if="column === 'price'"
    ><span class="nowrap">{{ priceText(row) }}</span></template
  >
  <template v-else-if="column === 'perMonth'"
    ><span class="nowrap">{{ periodPrice(row, 'month') }}</span></template
  >
  <template v-else-if="column === 'perYear'"
    ><span class="nowrap">{{ periodPrice(row, 'year') }}</span></template
  >
  <template v-else-if="column === 'reviewed'">
    <Tag
      v-if="row.reviewDue"
      severity="warn"
      value="due"
      :title="row.reviewDueOn ? `Due since ${day(row.reviewDueOn)}` : 'Due'"
    />
    <span v-else class="nowrap">{{ day(row.reviewedOn) }}</span>
  </template>
  <template v-else-if="column === 'updated'">
    <span class="muted nowrap" :title="formatAbsolute(row.updatedAt)">{{ formatDay(row.updatedAt) }}</span>
  </template>
</template>

<style scoped>
.line {
  display: block;
}

.warn {
  color: var(--pnmt-warn);
  white-space: nowrap;
}

.tone-warn {
  color: var(--pnmt-warn);
}

.tone-danger {
  color: var(--pnmt-fail);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}
</style>
