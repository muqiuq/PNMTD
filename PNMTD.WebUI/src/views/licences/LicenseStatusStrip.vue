<script setup lang="ts">
import { computed } from 'vue'
import { daysLeftText, expiryLabel, typeLabels, type LicenseSensorSummary } from '@/api/licenses'
import { useNow } from '@/composables/useNow'
import { formatDay } from '@/format'
import { useSession } from '@/stores/session'

/**
 * An item sensor's strip (docs/licences-plan.md §8.9), for everyone who sees the sensor: *Licence LIC-12 · Microsoft 365 Business
 * Standard · valid until 31 Dec 2026 · 35 days left*, the end of life and the seats, and *Open* for Licences & services: View. No
 * price, keys, note or documents: the summary has none.
 */
const props = defineProps<{ summary: LicenseSensorSummary }>()
const session = useSession()
const now = useNow()
const s = computed(() => props.summary)
const canOpen = computed(() => session.can('Licenses', 'View'))
</script>

<template>
  <section class="panel strip" aria-label="Licence status" data-test="license-strip">
    <dl class="stats">
      <div>
        <dt>{{ typeLabels[s.type] }}</dt>
        <dd>
          <span class="mono">{{ s.display }}</span> · {{ s.name }}
          <div v-if="s.manufacturer || s.productName" class="muted small">
            {{ [s.manufacturer, s.productName].filter(Boolean).join(' · ') }}
          </div>
        </dd>
      </div>
      <div>
        <dt>{{ s.expiresOn ? expiryLabel(s.kind) : 'Term' }}</dt>
        <dd>
          <template v-if="s.expiresOn">
            {{ formatDay(s.expiresOn) }}
            <span class="small muted"> · {{ daysLeftText(s.expiresOn, now) }}</span>
          </template>
          <span v-else class="muted">doesn't expire</span>
          <div v-if="s.status !== 'Active'" class="small muted">{{ s.status.toLowerCase() }}</div>
        </dd>
      </div>
      <div v-if="s.endOfLifeOn">
        <dt>End of life</dt>
        <dd>{{ formatDay(s.endOfLifeOn) }}</dd>
      </div>
      <div v-if="s.seats">
        <dt>Seats</dt>
        <dd>
          {{ s.usedSeats }} of {{ s.seats }} {{ s.seatUnit }}
          <div class="small muted">{{ s.assignmentSeats }} on this host</div>
        </dd>
      </div>
      <RouterLink
        v-if="canOpen"
        class="action p-button p-button-sm p-button-secondary"
        :to="{ name: 'licence', params: { number: String(s.number) } }"
        >Open</RouterLink
      >
    </dl>
  </section>
</template>

<style scoped>
.strip {
  padding: var(--pnmt-space-2) var(--pnmt-space-4);
}

.stats {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2) var(--pnmt-space-6);
  margin: 0;
  align-items: flex-start;
}

.stats dt {
  font-size: 0.786rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pnmt-muted);
  font-weight: 600;
}

.stats dd {
  margin: 0;
  font-weight: 500;
}

.small {
  font-size: 0.857rem;
  font-weight: 400;
}

.action {
  margin-left: auto;
  align-self: center;
  text-decoration: none;
}
</style>
