<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import ProgressSpinner from 'primevue/progressspinner'
import { computed } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import HexDump from '@/components/HexDump.vue'
import { formatAbsolute } from '@/format'
import { fromBase64, printableText } from '@/format/bytes'
import { resultStatusText, suiteName } from '@/format/udp'
import CommandLogTimeline from './CommandLogTimeline.vue'

/** An expanded command: its bytes, its result, and its communication from the permanent log. */
const props = defineProps<{ sensorId: string; command: S['UdpCommandItem'] }>()

const log = useQuery({
  queryKey: computed(() => [...keys.udpNode(props.sensorId), 'command-log', props.command.id, props.command.state]),
  queryFn: () => api.udp.commandLog(props.sensorId, props.command.id),
})
const payload = computed(() => fromBase64(props.command.payload))
const result = computed(() => (props.command.result ? fromBase64(props.command.result) : null))
const resultText = computed(() => (result.value ? printableText(result.value) : null))
</script>

<template>
  <div class="command-detail">
    <div class="bytes">
      <div>
        <div class="sub">
          Command <span class="muted">· CMD_ID {{ command.cmdId }} · {{ payload.length }} bytes</span>
        </div>
        <HexDump :bytes="payload" label="Command bytes" />
        <dl class="facts small">
          <dt>Delivery</dt>
          <dd>
            {{ command.delivery === 'FireAndForget' ? 'Fire and forget: sent once' : 'Until its result arrives' }}
          </dd>
          <dt>Suite</dt>
          <dd>{{ command.suite ? suiteName(command.suite) : "The node's response suite (as received by default)" }}</dd>
          <dt>Expires</dt>
          <dd>{{ command.expiresAt ? formatAbsolute(command.expiresAt) : 'never' }}</dd>
          <template v-if="command.lastSentAt">
            <dt>Last sent</dt>
            <dd>{{ formatAbsolute(command.lastSentAt) }}</dd>
          </template>
          <template v-if="command.note">
            <dt>Note</dt>
            <dd>{{ command.note }}</dd>
          </template>
        </dl>
      </div>
      <div v-if="command.resultStatus !== null">
        <div class="sub">
          Result
          <span :class="command.resultStatus ? 'bad' : 'muted'"
            >· {{ command.resultStatus }} {{ command.resultStatusText ?? resultStatusText(command.resultStatus) }}</span
          >
        </div>
        <HexDump v-if="result && result.length" :bytes="result" label="Result bytes" />
        <div v-else class="muted">No result bytes.</div>
        <div v-if="resultText" class="text">
          <span class="muted">UTF-8</span> <code>{{ resultText }}</code>
        </div>
      </div>
    </div>
    <div class="sub">Communication</div>
    <ErrorState v-if="log.isError.value" :error="log.error.value" what="the log" @retry="log.refetch()" />
    <div v-else-if="!log.data.value"><ProgressSpinner style="width: 24px; height: 24px" /></div>
    <CommandLogTimeline v-else :items="log.data.value" />
  </div>
</template>

<style scoped>
.command-detail {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) 0;
  max-width: 64rem;
}

.bytes {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(22rem, 1fr));
  gap: var(--pnmt-space-4);
}

.sub {
  font-weight: 500;
  margin-bottom: var(--pnmt-space-1);
}

.facts.small {
  margin-top: var(--pnmt-space-2);
  font-size: 0.929rem;
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
}

.bad {
  color: var(--pnmt-fail);
}

.text {
  margin-top: var(--pnmt-space-1);
  overflow-wrap: anywhere;
}

@media (max-width: 599px) {
  .bytes {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
