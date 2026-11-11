<script setup lang="ts">
import Button from 'primevue/button'
import SelectButton from 'primevue/selectbutton'
import { computed, ref } from 'vue'
import { api, keys, sensorRequest, type S } from '@/api/resources'
import CodeTag from '@/components/CodeTag.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import SinceText from '@/components/SinceText.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import UptimeBar from '@/components/UptimeBar.vue'
import { useActions } from '@/composables/useActions'
import { typeIcon, useStatusCodes } from '@/composables/useLookups'
import { useUndo } from '@/composables/useUndo'
import { useSession } from '@/stores/session'

/**
 * What is wrong now (§5.5): failing sensors that are not ignored, grouped by host, longest failing
 * first. Each shows the error message and since when it fails, which v0.12 never did.
 */
const props = defineProps<{ problems: S['ProblemHost'][]; sensorCount: number }>()
const session = useSession()
const codes = useStatusCodes()
const { run } = useActions()
const undo = useUndo()

const order = ref<'longest' | 'name'>('longest')
const orders = [
  { label: 'Longest', value: 'longest' },
  { label: 'Name', value: 'name' },
]

const oldest = (h: S['ProblemHost']) =>
  Math.min(...h.sensors.map((s) => (s.failingSince ? Date.parse(s.failingSince) : Number.MAX_SAFE_INTEGER)))

const hosts = computed(() =>
  [...props.problems]
    .map((h) => ({
      ...h,
      sensors: [...h.sensors].sort((a, b) =>
        order.value === 'name'
          ? a.name.localeCompare(b.name)
          : (Date.parse(a.failingSince ?? '') || 0) - (Date.parse(b.failingSince ?? '') || 0),
      ),
    }))
    .sort((a, b) => (order.value === 'name' ? a.name.localeCompare(b.name) : oldest(a) - oldest(b))),
)
const failing = computed(() => props.problems.reduce((n, h) => n + h.sensors.length, 0))

async function setIgnore(id: string, ignore: boolean) {
  const sensor = await api.sensors.get(id)
  await api.sensors.update(id, sensorRequest(sensor, { ignore }))
  return sensor
}

async function ignore(host: S['ProblemHost'], sensor: S['ProblemSensor']) {
  const done = await run(() => setIgnore(sensor.id, true), {
    failure: 'Not ignored',
    invalidate: [keys.sensor(sensor.id), keys.host(host.id), keys.hosts],
  })
  if (done) {
    undo(`${host.name} / ${sensor.name} is ignored`, () =>
      run(() => setIgnore(sensor.id, false), { invalidate: [keys.sensor(sensor.id), keys.host(host.id), keys.hosts] }),
    )
  }
}
</script>

<template>
  <section class="panel problems" aria-labelledby="problems-title">
    <header class="panel-header">
      <span id="problems-title">Problems</span>
      <span v-if="failing" class="count">{{ failing }}</span>
      <SelectButton
        v-if="failing > 1"
        v-model="order"
        :options="orders"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        class="order"
        aria-label="Order"
      />
    </header>
    <div v-if="!failing" class="all-good">
      <i class="pi pi-check-circle" aria-hidden="true" />
      No failures · {{ sensorCount }} sensors OK
    </div>
    <ul v-else class="hosts">
      <li v-for="host in hosts" :key="host.id" class="host">
        <RouterLink :to="{ name: 'host', params: { id: host.id } }" class="host-line">
          <StatusBadge status="Failing" icon-only :label="`${host.name}: ${host.sensors.length} failing`" />
          <span class="host-name">{{ host.name }}</span>
          <span class="muted">· {{ host.sensors.length }} failing</span>
          <span class="open muted">open <i class="pi pi-angle-right" /></span>
        </RouterLink>
        <ul class="sensors">
          <li v-for="sensor in host.sensors" :key="sensor.id" class="sensor">
            <div class="line1">
              <i :class="typeIcon(sensor.type)" class="type muted" :title="sensor.type" aria-hidden="true" />
              <RouterLink :to="{ name: 'sensor', params: { id: sensor.id } }" class="sensor-name">{{
                sensor.name
              }}</RouterLink>
              <CodeTag :code="sensor.code" :name="codes.nameOf(sensor.code)" />
              <span class="spacer" />
              <span class="since"><span class="muted">since</span> <SinceText :since="sensor.failingSince" /></span>
            </div>
            <div class="line2">
              <span class="message ellipsis" :title="sensor.message ?? ''">{{ sensor.message || '(no message)' }}</span>
              <span class="last muted">last <RelativeTime :value="sensor.lastEventAt" /></span>
              <UptimeBar
                v-if="sensor.last24Hours?.length"
                class="strip"
                :values="sensor.last24Hours"
                :minimum="24"
                :min-width="2"
                :height="12"
              />
              <Button
                v-if="session.can('User')"
                v-tooltip.left="'Ignore'"
                icon="pi pi-eye-slash"
                text
                rounded
                size="small"
                severity="secondary"
                class="ignore"
                :aria-label="`Ignore ${host.name} / ${sensor.name}`"
                @click="ignore(host, sensor)"
              />
            </div>
          </li>
        </ul>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.count {
  background: var(--pnmt-fail);
  color: #fff;
  border-radius: 999px;
  padding: 0 0.45rem;
  font-size: 0.786rem;
  line-height: 1.4;
}

.order {
  margin-left: auto;
}

.all-good {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-3);
  color: var(--pnmt-ok);
  font-weight: 500;
}

.hosts,
.sensors {
  list-style: none;
  margin: 0;
  padding: 0;
}

.host + .host {
  border-top: 1px solid var(--pnmt-border);
}

.host-line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) var(--pnmt-space-3) var(--pnmt-space-1);
  color: var(--pnmt-text);
}

.host-line:hover {
  text-decoration: none;
  background: var(--p-content-hover-background);
}

.host-name {
  font-weight: 600;
}

.open {
  margin-left: auto;
  font-size: 0.857rem;
}

.sensor {
  padding: var(--pnmt-space-1) var(--pnmt-space-3) var(--pnmt-space-2) calc(var(--pnmt-space-3) + 24px);
}

/* Wide enough: one line per sensor (name, code, message, since, last, 24 h). */
.problems {
  container-type: inline-size;
}

@container (min-width: 860px) {
  .sensor {
    display: flex;
    align-items: center;
    gap: var(--pnmt-space-3);
  }

  .line1 {
    flex: 0 0 auto;
    max-width: 50%;
  }

  .line1 .spacer {
    display: none;
  }

  .line2 {
    flex: 1 1 auto;
    margin-top: 0;
  }
}

.line1,
.line2 {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.line2 {
  margin-top: 1px;
  font-size: 0.929rem;
}

.type {
  font-size: 0.857rem;
  width: 1rem;
}

.sensor-name {
  font-weight: 500;
  white-space: nowrap;
}

.spacer {
  flex: 1;
}

.since {
  white-space: nowrap;
}

.message {
  flex: 1 1 auto;
}

.last {
  white-space: nowrap;
}

.strip {
  flex: 0 0 96px;
  width: 96px;
  min-width: 0;
}

.ignore {
  flex: 0 0 auto;
  width: 1.75rem;
  height: 1.75rem;
}

@media (max-width: 599px) {
  .sensor {
    padding-left: var(--pnmt-space-3);
  }

  .line1 {
    flex-wrap: wrap;
  }

  .strip,
  .last {
    display: none;
  }
}
</style>
