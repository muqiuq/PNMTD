<script setup lang="ts">
import Button from 'primevue/button'
import { computed } from 'vue'
import { money, upcomingLabels, type LicenseRow, type LicenseUpcomingEntry } from '@/api/licenses'
import { formatDay } from '@/format'
import { addDays } from './licenceList'
import VerdictTag from './VerdictTag.vue'

/**
 * One line of *Upcoming* (docs/licences-plan.md §8.4): the day, the item, what comes up, who hears about it (its hosts, or for an
 * item on no host its owner, or *nobody notified*), the price where there is one, and for Edit the action that fits: *Renew…* an
 * expiry, *Mark reviewed…* a review, *Cancel…* an automatic renewal. The actions need the item's row (`row`).
 */
const props = defineProps<{ entry: LicenseUpcomingEntry; row?: LicenseRow; overdue?: boolean; edit: boolean }>()
const emit = defineEmits<{ renew: [row: LicenseRow]; review: [row: LicenseRow]; cancel: [row: LicenseRow] }>()

const shortDay = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short' })
const day = computed(() => shortDay.format(new Date(`${props.entry.day}T12:00:00`)))

const what = computed(() => {
  const e = props.entry
  const label = upcomingLabels[e.what] ?? e.what
  if (props.overdue)
    return e.what === 'expired' ? `${label} on ${formatDay(e.day)}` : `${label} since ${formatDay(e.day)}`
  if (e.what === 'cancelBy' && props.row?.noticeDays !== null && props.row?.noticeDays !== undefined)
    return `${label} (renews on ${formatDay(addDays(e.day, props.row.noticeDays + 1))})`
  if (e.what === 'expires' && e.renewal !== 'None')
    return `${label} · ${e.renewal === 'Manual' ? 'manual' : 'automatic'}`
  return label
})

const who = computed(() => {
  const e = props.entry
  if (e.hosts) return `${e.hosts} host${e.hosts === 1 ? '' : 's'}`
  return `no host · ${e.nobodyNotified ? 'nobody notified' : (e.ownerName ?? 'no owner')}`
})

const manualExpiry = ['expires', 'warrantyEnds', 'leaseEnds']
const price = computed(() => {
  const e = props.entry
  if (e.price === null || !e.currency) return null
  const amount = money(e.price, e.currency)
  return e.renewal === 'Manual' && manualExpiry.includes(e.what) ? `${amount} if renewed` : amount
})

const canRenew = computed(() => ['expired', 'expires', 'ends', 'leaseEnds'].includes(props.entry.what))
const canReview = computed(() => props.entry.what === 'reviewDue')
const canCancel = computed(() => ['cancelBy', 'renews'].includes(props.entry.what) && props.row?.status === 'Active')
</script>

<template>
  <li class="entry" data-test="upcoming-entry">
    <span v-if="!overdue" class="day nowrap">{{ day }}</span>
    <span class="item">
      <VerdictTag :code="entry.verdictCode" compact />
      <RouterLink :to="{ name: 'licence', params: { number: String(entry.number) } }" class="nowrap"
        ><span class="mono">{{ entry.display }}</span> {{ entry.name }}</RouterLink
      >
    </span>
    <span class="what">{{ what }}</span>
    <span class="who muted" :class="{ warn: entry.nobodyNotified && !entry.hosts }">
      <i v-if="entry.nobodyNotified && !entry.hosts" class="pi pi-exclamation-triangle" aria-hidden="true" />
      {{ who }}
    </span>
    <span class="price nowrap">{{ price }}</span>
    <span v-if="edit && row" class="actions">
      <Button v-if="canRenew" label="Renew…" size="small" text @click="emit('renew', row)" />
      <Button v-if="canReview" label="Mark reviewed…" size="small" text @click="emit('review', row)" />
      <Button v-if="canCancel" label="Cancel…" size="small" severity="secondary" text @click="emit('cancel', row)" />
    </span>
  </li>
</template>

<style scoped>
.entry {
  display: grid;
  grid-template-columns: 4.5rem minmax(12rem, 2fr) minmax(10rem, 2fr) minmax(8rem, 1.2fr) minmax(7rem, 1fr) 9rem;
  align-items: center;
  gap: var(--pnmt-space-2) var(--pnmt-space-3);
  padding: var(--pnmt-space-1) var(--pnmt-space-3);
  min-height: 2.25rem;
}

.entry + .entry {
  border-top: 1px solid var(--pnmt-border);
}

.day {
  grid-column: 1;
}

.item {
  grid-column: 2;
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}

.what {
  grid-column: 3;
}

.who {
  grid-column: 4;
}

.price {
  grid-column: 5;
  text-align: right;
}

.actions {
  grid-column: 6;
  display: flex;
  justify-content: flex-end;
}

.warn {
  color: var(--pnmt-warn);
}

@media (max-width: 959px) {
  .entry {
    grid-template-columns: 4.5rem minmax(0, 1fr) auto;
  }

  .day {
    grid-column: 1;
  }

  .item,
  .what,
  .who {
    grid-column: 2;
  }

  .price,
  .actions {
    grid-column: 3;
  }
}
</style>
