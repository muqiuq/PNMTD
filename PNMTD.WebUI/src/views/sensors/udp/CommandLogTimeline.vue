<script setup lang="ts">
import Button from 'primevue/button'
import { ref } from 'vue'
import type { S } from '@/api/resources'
import HexDump from '@/components/HexDump.vue'
import PacketInspector from '@/components/PacketInspector.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { formatAbsolute } from '@/format'
import { fromBase64 } from '@/format/bytes'
import { kindIcon, kindLabel, kindSeverity, resultStatusText, suiteInfo, suiteName } from '@/format/udp'

/**
 * A command's or an update's communication, from the permanent command log (docs/udp-iot-plan.md
 * §6.4): "Queued by alice", "Sent, attempt 1, to 203.0.113.5:40123 with HMAC-SHA256/32, in answer
 * to FCNT 44", "Result received: OK". Each step with a datagram opens in the packet inspector.
 */
defineProps<{ items: S['UdpLogItem'][] }>()

const open = ref<Set<number>>(new Set())
function toggle(id: number) {
  const next = new Set(open.value)
  if (next.has(id)) next.delete(id)
  else next.add(id)
  open.value = next
}

const byUser = new Set(['Queued', 'Cancelled', 'OtaQueued', 'OtaCancelled'])

function describe(l: S['UdpLogItem']): string {
  const parts = [kindLabel(l.kind) + (byUser.has(l.kind) && l.userName ? ` by ${l.userName}` : '')]
  if (l.attempt) parts.push(`attempt ${l.attempt}`)
  if (l.endpoint) parts.push(`${l.direction === 'Up' ? 'from' : 'to'} ${l.endpoint}`)
  if (l.suite) parts.push(`with ${suiteName(l.suite)}`)
  if (l.fcnt !== null && l.fcnt !== undefined) {
    parts.push(l.direction === 'Down' ? `in answer to FCNT ${l.fcnt}` : `FCNT ${l.fcnt}`)
  }
  if (l.resultStatus !== null && l.resultStatus !== undefined) {
    parts.push(`${l.resultStatus === 0 ? '' : 'status '}${resultStatusText(l.resultStatus)}`)
  }
  return parts.join(', ')
}

const encrypted = (l: S['UdpLogItem']) => !!suiteInfo(l.suite)?.encrypted
const payloadLabel = (l: S['UdpLogItem']) =>
  l.direction === 'Up' ? 'STATUS and RESULT (decrypted)' : 'Command bytes (CMD_ID not included)'
</script>

<template>
  <ol class="timeline">
    <li v-for="l in items" :key="l.id" :class="`tone-${kindSeverity(l.kind)}`">
      <i :class="kindIcon(l.kind)" class="dot" aria-hidden="true" />
      <div class="step">
        <div class="line">
          <span class="what">{{ describe(l) }}</span>
          <span class="when muted" :title="formatAbsolute(l.created)"><RelativeTime :value="l.created" /></span>
          <Button
            v-if="l.datagram"
            :label="open.has(l.id) ? 'Hide bytes' : 'Bytes'"
            :icon="open.has(l.id) ? 'pi pi-chevron-up' : 'pi pi-chevron-down'"
            size="small"
            text
            :aria-expanded="open.has(l.id)"
            @click="toggle(l.id)"
          />
        </div>
        <div v-if="l.note" class="muted note">{{ l.note }}</div>
        <div v-if="l.datagram && open.has(l.id)" class="inspect">
          <PacketInspector
            :datagram="l.datagram"
            :fields="l.fields"
            :payload="encrypted(l) ? l.payload : null"
            :payload-label="encrypted(l) ? payloadLabel(l) : undefined"
            :encrypted="encrypted(l)"
          />
        </div>
        <div v-else-if="!l.datagram && l.payload && l.kind === 'Queued'" class="inspect">
          <HexDump :bytes="fromBase64(l.payload)" label="Command bytes" />
        </div>
      </div>
    </li>
    <li v-if="!items.length" class="muted empty">Nothing logged yet.</li>
  </ol>
</template>

<style scoped>
.timeline {
  list-style: none;
  margin: 0;
  padding: 0;
  position: relative;
}

.timeline li {
  display: flex;
  gap: var(--pnmt-space-2);
  position: relative;
  padding-bottom: var(--pnmt-space-2);
}

/* The line between the steps. */
.timeline li:not(:last-child)::before {
  content: '';
  position: absolute;
  left: 0.6rem;
  top: 1.5rem;
  bottom: 0;
  border-left: 1px solid var(--pnmt-border);
}

.dot {
  flex: none;
  width: 1.25rem;
  height: 1.25rem;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  font-size: 0.75rem;
  margin-top: 0.3rem;
  border-radius: 50%;
  background: var(--pnmt-none-soft);
  color: var(--pnmt-muted);
}

.tone-success .dot {
  background: var(--pnmt-ok-soft);
  color: var(--pnmt-ok);
}

.tone-danger .dot {
  background: var(--pnmt-fail-soft);
  color: var(--pnmt-fail);
}

.tone-info .dot {
  color: var(--p-primary-color);
}

.step {
  flex: 1;
  min-width: 0;
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
  min-height: 32px;
}

.what {
  overflow-wrap: anywhere;
}

.when {
  font-size: 0.857rem;
  white-space: nowrap;
}

.note {
  font-size: 0.929rem;
}

.inspect {
  margin: var(--pnmt-space-1) 0 var(--pnmt-space-2);
  max-width: 60rem;
}

.empty {
  padding: var(--pnmt-space-2) 0;
}
</style>
