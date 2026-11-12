<script setup lang="ts">
import Message from 'primevue/message'
import ProgressBar from 'primevue/progressbar'
import { computed } from 'vue'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNow } from '@/composables/useNow'
import { formatPercent } from '@/format'
import { formatBytes } from '@/format/bytes'
import { nextExpected, percent, suiteName, words } from '@/format/udp'

/**
 * A node's state at a glance (docs/udp-iot-plan.md §6.2): last seen and next expected, where from
 * and in which suite, the counter and the session, the last 24 hours, the last restart, the
 * firmware with an open update's progress, and the server's warnings.
 */
const props = defineProps<{ sensor: S['SensorDetail'] }>()
const now = useNow()

const udp = computed(() => props.sensor.udp!)
const next = computed(() => nextExpected(udp.value.lastSeenAt, props.sensor.interval))
const late = computed(() => {
  const n = next.value
  return n !== null && now.value > n.getTime() + props.sensor.gracePeriod * 1000
})
const session = computed(() => {
  const u = udp.value
  if (u.sessionMode === 'None') return u.sessionActive ? 'active (no JOIN needed)' : 'none (persistent counter)'
  if (u.sessionActive) return u.sessionPending ? 'active, a JOIN pending' : 'active'
  return u.sessionPending ? 'JOIN accepted, waiting for the first packet' : 'none: the device must JOIN'
})
const numbers = computed(() => udp.value.last24Hours)
const reasons = computed(() => numbers.value.rejectedByReason.map((r) => `${words(r.reason)} ${r.count}`).join(', '))
</script>

<template>
  <section class="panel strip" aria-label="Node status">
    <dl class="stats">
      <div>
        <dt>Last seen</dt>
        <dd>
          <RelativeTime :value="udp.lastSeenAt" />
          <span v-if="udp.lastEndpoint" class="muted small">
            · <span class="mono">{{ udp.lastEndpoint }}</span> · {{ suiteName(udp.lastSuite) }}</span
          >
        </dd>
      </div>
      <div>
        <dt>Next expected</dt>
        <dd :class="{ late }">
          <RelativeTime v-if="next" :value="next.toISOString()" />
          <span v-else class="muted">after the first packet</span>
          <span v-if="late" class="small"> · missing</span>
        </dd>
      </div>
      <div>
        <dt>Counter</dt>
        <dd class="mono">{{ udp.fcnt ?? '–' }}</dd>
      </div>
      <div>
        <dt>Session</dt>
        <dd>{{ session }}</dd>
      </div>
      <div>
        <dt>24 h</dt>
        <dd>
          {{ numbers.accepted.toLocaleString() }} packets ·
          <span :title="'Loss from the gaps in the counter'">{{ formatPercent(numbers.loss, 1) }} loss</span> ·
          <span :class="{ bad: numbers.rejected > 0 }" :title="reasons || undefined"
            >{{ numbers.rejected.toLocaleString() }} rejected</span
          >
          <div v-if="reasons" class="muted small">{{ reasons }}</div>
          <div class="muted small">{{ formatBytes(numbers.bytesIn) }} in · {{ formatBytes(numbers.bytesOut) }} out</div>
        </dd>
      </div>
      <div>
        <dt>Last restart</dt>
        <dd>
          <RelativeTime :value="udp.lastBootAt" />
          <span v-if="udp.lastBootReason" class="muted small"> · {{ udp.lastBootReason }}</span>
        </dd>
      </div>
      <div>
        <dt>Firmware</dt>
        <dd>
          <span class="mono">{{ udp.firmwareVersion ?? '–' }}</span>
          <div v-if="udp.openUpdate" class="update">
            <span class="small"
              >{{ words(udp.openUpdate.state) }}: {{ udp.openUpdate.firmwareName }}
              {{ udp.openUpdate.firmwareVersion }}</span
            >
            <ProgressBar
              :value="percent(udp.openUpdate.progress)"
              :show-value="false"
              class="bar"
              :aria-label="`Update progress ${percent(udp.openUpdate.progress)} %`"
            />
          </div>
        </dd>
      </div>
    </dl>
    <div v-if="udp.warnings.length" class="warnings">
      <Message v-for="w in udp.warnings" :key="w" severity="warn" :closable="false" size="small">{{ w }}</Message>
    </div>
  </section>
</template>

<style scoped>
.strip {
  padding: var(--pnmt-space-2) var(--pnmt-space-4);
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.stats {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2) var(--pnmt-space-6);
  margin: 0;
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

.late,
.bad {
  color: var(--pnmt-fail);
}

.update {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 10rem;
}

.bar {
  height: 6px;
}

.warnings {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

@media (max-width: 599px) {
  .strip {
    padding: var(--pnmt-space-2) var(--pnmt-space-3);
  }

  .stats {
    gap: var(--pnmt-space-2) var(--pnmt-space-4);
  }
}
</style>
