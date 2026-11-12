<script setup lang="ts">
import Button from 'primevue/button'
import ProgressBar from 'primevue/progressbar'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { formatAbsolute, formatDuration } from '@/format'
import { formatBytes } from '@/format/bytes'
import { downloadRate, isOpenUpdate, otaSeverity, percent, words } from '@/format/udp'
import CommandLogTimeline from './CommandLogTimeline.vue'

/**
 * One firmware update of a node (docs/udp-iot-plan.md §6.2): its progress (the highest offset
 * fetched of the image's size), what was served and fetched again, the rate and the time left
 * while downloading, the estimate, and its milestones from the command log.
 */
const props = defineProps<{ job: S['UdpOtaJobItem']; log?: S['UdpLogItem'][] | null; canCancel: boolean }>()
const emit = defineEmits<{ cancel: [job: S['UdpOtaJobItem']] }>()

const showLog = ref(false)
const rate = computed(() => downloadRate(props.job))
const open = computed(() => isOpenUpdate(props.job.state))
</script>

<template>
  <article class="panel job">
    <header class="panel-header">
      <span
        >{{ job.firmwareName }} <span class="mono">{{ job.firmwareVersion }}</span></span
      >
      <Tag :value="words(job.state)" :severity="otaSeverity(job.state)" />
      <span class="muted small"
        >update {{ job.updateId }} · queued <RelativeTime :value="job.created" /><template v-if="job.createdBy">
          by {{ job.createdBy }}</template
        ></span
      >
      <Button
        v-if="canCancel && open"
        label="Cancel"
        icon="pi pi-times"
        size="small"
        text
        severity="danger"
        class="end"
        @click="emit('cancel', job)"
      />
    </header>
    <div class="panel-body body">
      <div class="progress">
        <ProgressBar
          :value="percent(job.progress)"
          :show-value="false"
          class="bar"
          :aria-label="`${percent(job.progress)} %`"
        />
        <span class="small"
          >{{ percent(job.progress) }} % · {{ formatBytes(job.highestOffset) }} of
          {{ formatBytes(job.firmwareSize) }}</span
        >
      </div>
      <dl class="facts">
        <dt>Served</dt>
        <dd>
          {{ formatBytes(job.bytesServed) }} in {{ job.chunksServed.toLocaleString() }} chunks of {{ job.chunkSize }} B
          · {{ job.chunkRetransmissions.toLocaleString() }} fetched again
        </dd>
        <template v-if="rate">
          <dt>Rate</dt>
          <dd>
            {{ formatBytes(Math.round(rate.bytesPerSecond)) }}/s · about
            {{ formatDuration(rate.secondsLeft * 1000) }} left
          </dd>
        </template>
        <dt>Offers</dt>
        <dd>
          {{ job.offerAttempts }} of {{ job.maxOfferAttempts
          }}<template v-if="job.offeredAt"> · last <RelativeTime :value="job.offeredAt" /></template>
          <template v-if="open"> · expires <RelativeTime :value="job.expiresAt" /></template>
        </dd>
        <dt>Estimate</dt>
        <dd>
          {{ job.estimate.roundTrips.toLocaleString() }} round trips, {{ formatBytes(job.estimate.bytesOnTheWire) }} on
          the wire, about {{ formatDuration(job.estimate.secondsAt100Ms * 1000) }} at 100 ms or
          {{ formatDuration(job.estimate.secondsAt2s * 1000) }} at 2 s per round trip
        </dd>
        <template v-if="job.startedAt">
          <dt>Started</dt>
          <dd>{{ formatAbsolute(job.startedAt) }}</dd>
        </template>
        <template v-if="job.finishedAt">
          <dt>Finished</dt>
          <dd>
            {{ formatAbsolute(job.finishedAt)
            }}<template v-if="job.appliedVersion">
              · running <span class="mono">{{ job.appliedVersion }}</span></template
            >
          </dd>
        </template>
        <template v-if="job.reasonText || job.reason !== null">
          <dt>Reason</dt>
          <dd class="bad">{{ job.reasonText ?? job.reason }}</dd>
        </template>
        <dt>SHA-256</dt>
        <dd class="mono hash">{{ job.firmwareSha256 }}</dd>
      </dl>
      <template v-if="log">
        <Button
          :label="showLog ? 'Hide communication' : `Communication (${log.length})`"
          :icon="showLog ? 'pi pi-chevron-up' : 'pi pi-chevron-down'"
          size="small"
          text
          class="toggle"
          :aria-expanded="showLog"
          @click="showLog = !showLog"
        />
        <CommandLogTimeline v-if="showLog" :items="log" />
      </template>
    </div>
  </article>
</template>

<style scoped>
.job .panel-header {
  flex-wrap: wrap;
  padding-top: var(--pnmt-space-1);
  padding-bottom: var(--pnmt-space-1);
}

.end {
  margin-left: auto;
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.progress {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
}

.bar {
  flex: 1;
  height: 8px;
}

.small {
  font-size: 0.857rem;
  font-weight: 400;
  white-space: nowrap;
}

.facts {
  font-size: 0.929rem;
  gap: var(--pnmt-space-1) var(--pnmt-space-4);
}

.hash {
  overflow-wrap: anywhere;
  font-size: 0.786rem;
}

.bad {
  color: var(--pnmt-fail);
}

.toggle {
  align-self: flex-start;
}
</style>
