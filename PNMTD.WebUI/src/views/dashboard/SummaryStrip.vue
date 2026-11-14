<script setup lang="ts">
import { computed } from 'vue'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useSession } from '@/stores/session'

/**
 * One row of health (§5.5): the overall state as a coloured bar, then explicit counts, including what
 * v0.12 never showed (uplink, background tasks, DNS, the mailbox). Every tile opens its list.
 */
const props = defineProps<{ summary: S['DashboardSummary'] }>()
const session = useSession()

const failingSensors = computed(() => props.summary.sensors.failing)
const degraded = computed(
  () =>
    (props.summary.uplink.configured && !props.summary.uplink.isUp) ||
    props.summary.tasks.late > 0 ||
    props.summary.dns.mismatch > 0,
)
const tone = computed(() => (failingSensors.value > 0 ? 'fail' : degraded.value ? 'warn' : 'ok'))
const headline = computed(() => {
  const h = props.summary.hosts
  if (h.failing > 0) {
    return `${h.failing} host${h.failing === 1 ? '' : 's'} failing`
  }
  return degraded.value ? 'Hosts OK, health degraded' : `All ${h.total - h.disabled} hosts OK`
})
const admin = computed(() => session.can('System', 'Manage'))
const tasks = computed(() => props.summary.tasks)
</script>

<template>
  <section class="summary panel" :class="`tone-${tone}`" aria-label="Summary">
    <RouterLink :to="{ name: 'hosts', query: summary.hosts.failing ? { state: 'Failing' } : {} }" class="tile headline">
      <span class="big">{{ headline }}</span>
      <span class="muted">{{ summary.hosts.total }} hosts · {{ summary.hosts.disabled }} off</span>
    </RouterLink>
    <RouterLink :to="{ name: 'events', query: { status: 'failed', range: '24h' } }" class="tile sensors">
      <span class="label">Sensors</span>
      <span class="counts">
        <span class="ok">{{ summary.sensors.ok }} ok</span>
        <span :class="{ fail: summary.sensors.failing }">{{ summary.sensors.failing }} failing</span>
        <span>{{ summary.sensors.noData }} no data</span>
        <span>{{ summary.sensors.ignored }} ignored</span>
        <span>{{ summary.sensors.disabled }} off</span>
      </span>
    </RouterLink>
    <component :is="admin ? 'RouterLink' : 'div'" :to="admin ? { name: 'system' } : undefined" class="tile">
      <span class="label">Uplink</span>
      <span v-if="!summary.uplink.configured" class="muted">not configured</span>
      <span v-else-if="summary.uplink.isUp" class="ok"><i class="pi pi-check-circle" /> up</span>
      <span v-else class="warn"><i class="pi pi-exclamation-triangle" /> down</span>
    </component>
    <component :is="admin ? 'RouterLink' : 'div'" :to="admin ? { name: 'system' } : undefined" class="tile">
      <span class="label">Tasks</span>
      <span v-if="tasks.late" class="warn"><i class="pi pi-exclamation-triangle" /> {{ tasks.late }} late</span>
      <span v-else-if="tasks.ok" class="ok"
        ><i class="pi pi-check-circle" /> ok <RelativeTime :value="tasks.lastRunAt" class="muted"
      /></span>
      <span v-else class="muted">not running</span>
    </component>
    <RouterLink :to="{ name: 'dns-zones' }" class="tile">
      <span class="label">DNS</span>
      <span v-if="!summary.dns.zones" class="muted">no zones</span>
      <span v-else
        >{{ summary.dns.ok }} ok<template v-if="summary.dns.mismatch">
          · <span class="warn">{{ summary.dns.mismatch }} mismatch</span></template
        ></span
      >
    </RouterLink>
    <component :is="session.can('Mail', 'Read') ? 'RouterLink' : 'div'" :to="{ name: 'mail-log' }" class="tile">
      <span class="label">Mail</span>
      <span v-if="!summary.mail.configured" class="muted">not configured</span>
      <span v-else>checked <RelativeTime :value="summary.mail.lastCheckAt" /></span>
    </component>
  </section>
</template>

<style scoped>
.summary {
  display: flex;
  align-items: stretch;
  min-height: 60px;
  border-left-width: 5px;
  overflow: hidden;
}

.tone-ok {
  border-left-color: var(--pnmt-ok);
}

.tone-warn {
  border-left-color: var(--pnmt-warn);
}

.tone-fail {
  border-left-color: var(--pnmt-fail);
}

.tile {
  display: flex;
  flex-direction: column;
  justify-content: center;
  gap: 2px;
  padding: var(--pnmt-space-2) var(--pnmt-space-4);
  border-right: 1px solid var(--pnmt-border);
  color: var(--pnmt-text);
  min-width: 0;
  white-space: nowrap;
}

.tile:last-child {
  border-right: none;
}

a.tile:hover {
  background: var(--p-content-hover-background);
  text-decoration: none;
}

.headline {
  flex: 0 0 auto;
}

.big {
  font-size: 1.143rem;
  font-weight: 600;
}

.tone-fail .big {
  color: var(--pnmt-fail);
}

.tone-warn .big {
  color: var(--pnmt-warn);
}

.tone-ok .big {
  color: var(--pnmt-ok);
}

.sensors {
  flex: 1 1 auto;
}

.label {
  font-size: 0.786rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pnmt-muted);
}

.counts {
  display: flex;
  flex-wrap: wrap;
  gap: 0 var(--pnmt-space-3);
}

.ok {
  color: var(--pnmt-ok);
}

.fail {
  color: var(--pnmt-fail);
  font-weight: 600;
}

.warn {
  color: var(--pnmt-warn);
  font-weight: 500;
}

@media (max-width: 1199px) {
  .summary {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }

  .tile {
    border-bottom: 1px solid var(--pnmt-border);
  }

  .headline,
  .sensors {
    grid-column: span 3;
  }
}

@media (max-width: 599px) {
  .summary {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .headline,
  .sensors {
    grid-column: span 2;
  }

  .tile {
    padding: var(--pnmt-space-2) var(--pnmt-space-3);
    white-space: normal;
  }
}
</style>
