<script setup lang="ts">
import Message from 'primevue/message'
import { computed } from 'vue'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import { formatBytes } from '@/format/bytes'
import { words } from '@/format/udp'

/**
 * The listener (docs/udp-iot-plan.md §6.6): on or off, the endpoint and a bind error, packets per
 * minute, accepted and rejected by reason, the queue, and the rejected-packets warnings.
 */
const props = defineProps<{ status: S['UdpStatus'] }>()

const badge = computed(() =>
  !props.status.enabled
    ? { status: 'Disabled' as const, label: 'Off' }
    : props.status.listening
      ? { status: 'Ok' as const, label: 'Listening' }
      : { status: 'Degraded' as const, label: 'Not listening' },
)
const rejected = computed(() => props.status.rejected.reduce((sum, r) => sum + r.count, 0))
const dropped = computed(() => props.status.rejected.find((r) => r.reason === 'QueueFull')?.count ?? 0)
</script>

<template>
  <section class="panel listener" aria-labelledby="listener-title">
    <div id="listener-title" class="panel-header">
      Listener
      <StatusBadge v-bind="badge" />
      <code class="endpoint">{{ status.endpoint }}/udp</code>
      <span v-if="status.since" class="muted since">since <RelativeTime :value="status.since" /></span>
    </div>
    <div class="panel-body body">
      <Message v-if="!status.enabled" severity="info" :closable="false">
        Turned off with Udp__Enabled=false. Remove the setting (or set it to true) and restart PNMTD to turn it on.
      </Message>
      <Message v-else-if="status.error" severity="error" :closable="false">{{ status.error }}</Message>
      <dl class="stats">
        <div>
          <dt>Packets / min</dt>
          <dd>{{ status.packetsLastMinute.toLocaleString() }}</dd>
        </div>
        <div>
          <dt>Received</dt>
          <dd>{{ status.received.toLocaleString() }}</dd>
        </div>
        <div>
          <dt>Accepted</dt>
          <dd>{{ status.accepted.toLocaleString() }}</dd>
        </div>
        <div>
          <dt>Rejected</dt>
          <dd :class="{ bad: rejected > 0 }">{{ rejected.toLocaleString() }}</dd>
        </div>
        <div>
          <dt>Responses</dt>
          <dd>{{ status.responses.toLocaleString() }}</dd>
        </div>
        <div>
          <dt>In / out</dt>
          <dd>{{ formatBytes(status.bytesIn) }} / {{ formatBytes(status.bytesOut) }}</dd>
        </div>
        <div>
          <dt>Queue</dt>
          <dd>
            {{ status.queueDepth.toLocaleString()
            }}<span v-if="dropped" class="bad small"> · {{ dropped }} dropped</span>
          </dd>
        </div>
        <div>
          <dt>Nodes</dt>
          <dd>{{ status.nodes.toLocaleString() }}</dd>
        </div>
        <div>
          <dt>Firmware cache</dt>
          <dd>{{ status.firmwareCached }} · {{ formatBytes(status.firmwareCachedBytes) }}</dd>
        </div>
      </dl>
      <ul v-if="status.rejected.length" class="reasons" aria-label="Rejected by reason">
        <li v-for="r in status.rejected" :key="r.reason">
          <span>{{ words(r.reason) }}</span> <strong>{{ r.count.toLocaleString() }}</strong>
        </li>
      </ul>
      <Message
        v-for="w in status.warnings"
        :key="w.sensorId + w.message"
        severity="warn"
        :closable="false"
        size="small"
      >
        <RouterLink :to="{ name: 'sensor', params: { id: w.sensorId } }">{{ w.nodeName }}</RouterLink>: {{ w.message }}
      </Message>
      <p class="muted small note">
        Counted since the start of PNMTD; nothing about rejected packets is written to the database.
      </p>
    </div>
  </section>
</template>

<style scoped>
.listener .panel-header {
  flex-wrap: wrap;
}

.endpoint {
  font-weight: 400;
}

.since {
  font-weight: 400;
  font-size: 0.857rem;
  margin-left: auto;
}

.body {
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
  font-weight: 600;
  font-size: 1.071rem;
  white-space: nowrap;
}

.reasons {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
  list-style: none;
  margin: 0;
  padding: 0;
}

.reasons li {
  font-size: 0.857rem;
  padding: 0.1rem 0.5rem;
  border-radius: var(--p-border-radius-sm);
  background: var(--pnmt-fail-soft);
}

.bad {
  color: var(--pnmt-fail);
}

.small {
  font-size: 0.857rem;
  font-weight: 400;
}

.note {
  margin: 0;
}
</style>
