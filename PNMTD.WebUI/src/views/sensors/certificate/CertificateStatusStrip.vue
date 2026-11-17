<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import { computed } from 'vue'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNow } from '@/composables/useNow'
import { formatAbsolute, formatDay } from '@/format'
import { daysLeftText, intervalLabel, trustLabel, validityHeadline } from './certificate'

/**
 * An SSL certificate sensor at a glance (docs/ssl-certificate-plan.md §8.3), from the summary, for everyone who sees the
 * sensor: the endpoint and the trust, *valid until … · 76 days left* (counted live), the issuer's organisation, *checked 3 h
 * ago · next in 21 h* or *checking…*, the last error, and *Check now* for Certificates: Edit.
 */
const props = withDefaults(
  defineProps<{
    sensor: S['SensorDetail']
    canCheck: boolean
    checking: boolean
    /** The last error; the Certificate tab shows it itself. */
    showError?: boolean
  }>(),
  { showError: true },
)
defineEmits<{ check: [] }>()
const c = computed(() => props.sensor.certificate!)
const now = useNow()
const headline = computed(() => validityHeadline(c.value.expiresAt, c.value.warnDays, now.value))
const left = computed(() => daysLeftText(c.value.expiresAt, now.value))
const endpoint = computed(() => props.sensor.parameters ?? `${c.value.host}:${c.value.port}`)
const nextIsDue = computed(() => !c.value.nextCheckAt || Date.parse(c.value.nextCheckAt) <= now.value)
</script>

<template>
  <section class="panel strip" aria-label="Certificate status">
    <dl class="stats">
      <div>
        <dt>Endpoint</dt>
        <dd>
          <span class="mono">{{ endpoint }}</span>
          <div class="muted small">
            {{ trustLabel(c) }}<template v-if="c.serverName"> · SNI {{ c.serverName }}</template
            ><template v-if="c.connectTo"> · via {{ c.connectTo }}</template>
          </div>
        </dd>
      </div>
      <div>
        <dt>Valid until</dt>
        <dd>
          <template v-if="c.expiresAt">
            <span :title="formatAbsolute(c.expiresAt)">{{ formatDay(new Date(c.expiresAt)) }}</span>
            <span class="small" :class="`tone-${headline.tone}`"> · {{ left }}</span>
          </template>
          <span v-else class="muted">no certificate read yet</span>
          <div v-if="c.subjectCommonName" class="muted small mono">{{ c.subjectCommonName }}</div>
        </dd>
      </div>
      <div v-if="c.issuerOrganization || c.issuer || c.selfSigned">
        <dt>Issuer</dt>
        <dd>
          {{ c.selfSigned ? 'self-signed' : (c.issuerOrganization ?? c.issuer) }}
          <div v-if="c.issuer && !c.selfSigned && c.issuer !== c.issuerOrganization" class="muted small">
            {{ c.issuer }}
          </div>
        </dd>
      </div>
      <div>
        <dt>Checked</dt>
        <dd>
          <span v-if="c.checking || checking">checking…</span>
          <RelativeTime v-else :value="c.lastCheckAt" empty="not yet" />
          <div class="muted small">
            {{ intervalLabel(sensor.interval).toLowerCase() }} ·
            <template v-if="nextIsDue">due now</template>
            <template v-else>next <RelativeTime :value="c.nextCheckAt" /></template>
          </div>
        </dd>
      </div>
      <div v-if="canCheck" class="action">
        <Button
          label="Check now"
          icon="pi pi-refresh"
          size="small"
          outlined
          :loading="checking || c.checking"
          @click="$emit('check')"
        />
      </div>
    </dl>
    <Message
      v-if="showError && c.consecutiveFailures > 0 && c.lastError"
      severity="error"
      :closable="false"
      size="small"
    >
      {{ c.lastError }}<template v-if="c.firstFailureAt"> · since <RelativeTime :value="c.firstFailureAt" /></template
      ><template v-if="c.consecutiveFailures === 1">
        · one failed check: it is retried before the sensor fails</template
      >
    </Message>
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

.action {
  margin-left: auto;
  align-self: center;
}

.small {
  font-size: 0.857rem;
  font-weight: 400;
}

.tone-ok {
  color: var(--pnmt-ok);
}

.tone-warn {
  color: var(--pnmt-warn);
}

.tone-fail {
  color: var(--pnmt-fail);
}

@media (max-width: 599px) {
  .strip {
    padding: var(--pnmt-space-2) var(--pnmt-space-3);
  }

  .stats {
    gap: var(--pnmt-space-2) var(--pnmt-space-4);
  }

  .action {
    margin-left: 0;
  }
}
</style>
