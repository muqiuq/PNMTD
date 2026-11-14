<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Message from 'primevue/message'
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api, keys, sensorRequest, zabbixRequest, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { formatTime } from '@/format'
import { useSession } from '@/stores/session'
import KbPanel from '@/views/kb/KbPanel.vue'
import RedminePanel from '@/views/redmine/RedminePanel.vue'
import ZabbixInterfaces from '@/views/zabbix/ZabbixInterfaces.vue'
import ZabbixProblemList from '@/views/zabbix/ZabbixProblemList.vue'
import { severityOptions } from '@/views/zabbix/zabbix'

/**
 * A ZABBIX sensor's pages (docs/audit-redmine-zabbix-plan.md §7.7): *Overview*, its Zabbix host's problems (and which the
 * rules count), availability and links, with the ticket and knowledge base panels; *Setup*, the rules and *Detach*.
 * *Events* is the sensor page every type has.
 */
const props = defineProps<{ sensor: S['SensorDetail']; tab: 'overview' | 'setup'; canWrite: boolean }>()
const session = useSession()
const router = useRouter()
const queryClient = useQueryClient()
const { run, done, ask } = useActions()
const z = computed(() => props.sensor.zabbix!)

// --- setup -----------------------------------------------------------------------------------------------------------
const draft = ref(zabbixRequest(z.value))
watch(z, (value) => (draft.value = zabbixRequest(value)))
const changed = computed(() => JSON.stringify(draft.value) !== JSON.stringify(zabbixRequest(z.value)))
async function save() {
  const updated = await run(
    () => api.sensors.update(props.sensor.id, sensorRequest(props.sensor, { zabbix: draft.value })),
    {
      success: 'Saved: the sensor shows what the new rules make of its problems',
      failure: 'Not saved',
      invalidate: [keys.host(props.sensor.host.id), keys.hosts, keys.zabbix],
    },
  )
  if (updated) queryClient.setQueryData(keys.sensor(props.sensor.id), updated)
}
async function detach() {
  if (
    !(await ask(
      `Detach ${z.value.name}?`,
      'The sensor and its events are deleted; the host stays in Zabbix.',
      'Detach',
    ))
  )
    return
  if (
    await done(() => api.sensors.remove(props.sensor.id), {
      success: `${z.value.name} detached`,
      invalidate: [keys.host(props.sensor.host.id), keys.hosts, keys.zabbix],
    })
  ) {
    await router.replace({ name: 'host', params: { id: props.sensor.host.id } })
  }
}
</script>

<template>
  <div class="zabbix-tab">
    <Message v-if="z.error" severity="warn" icon="pi pi-exclamation-triangle">
      {{ z.error }}. The sensor keeps its state until Zabbix answers.
    </Message>
    <template v-if="tab === 'overview'">
      <div class="two-columns">
        <div class="column">
          <section class="panel" aria-labelledby="zs-problems-title">
            <div id="zs-problems-title" class="panel-header">
              Problems
              <span class="muted count">{{ z.problems.filter((p) => p.counted).length }} counted</span>
            </div>
            <div class="panel-body">
              <ZabbixProblemList
                :problems="z.problems.map((p) => p.problem)"
                :counted="z.problems.map((p) => p.counted)"
              />
            </div>
          </section>
          <KbPanel :sensor-id="sensor.id" :name="sensor.name" />
        </div>
        <div class="column">
          <section class="panel" aria-labelledby="zs-host-title">
            <div id="zs-host-title" class="panel-header">Zabbix host</div>
            <div class="panel-body">
              <p class="none">
                <RouterLink
                  v-if="session.can('Zabbix', 'View')"
                  :to="{ name: 'zabbix-host', params: { hostId: z.zabbixHostId } }"
                  >{{ z.name }}</RouterLink
                >
                <span v-else>{{ z.name }}</span>
                <span v-if="z.technicalName && z.technicalName !== z.name" class="mono muted">
                  · {{ z.technicalName }}</span
                >
              </p>
              <p v-if="z.gone" class="error none">Gone from Zabbix: deleted, or out of PNMT's host groups.</p>
              <p v-else-if="!z.monitored" class="error none">Disabled in Zabbix.</p>
              <p v-if="z.inMaintenance" class="muted none">In maintenance: its problems are suppressed.</p>
              <ZabbixInterfaces :interfaces="z.interfaces" detailed class="interfaces" />
              <div v-if="z.links" class="links">
                <Button
                  as="a"
                  :href="z.links.open"
                  target="_blank"
                  rel="noopener noreferrer"
                  label="Open in Zabbix"
                  icon="pi pi-external-link"
                  size="small"
                  outlined
                />
                <Button
                  as="a"
                  :href="z.links.latest"
                  target="_blank"
                  rel="noopener noreferrer"
                  label="Latest data"
                  size="small"
                  text
                />
              </div>
              <p class="muted small">
                Fails from {{ severityOptions.find((o) => o.value === z.minSeverity)?.label
                }}<template v-if="!z.countAcknowledged">, acknowledged problems not counted</template
                ><template v-if="z.countSuppressed">, suppressed ones counted</template
                ><template v-if="z.unavailableFails">, and when an interface is unavailable</template>.
                <template v-if="z.asOf">As of {{ formatTime(z.asOf) }}.</template>
              </p>
            </div>
          </section>
          <RedminePanel
            :sensor-id="sensor.id"
            :name="`${sensor.host.name} / ${sensor.name}`"
            :path="`/sensors/${sensor.id}`"
          />
        </div>
      </div>
    </template>

    <section v-else class="panel" aria-labelledby="zs-setup-title">
      <div id="zs-setup-title" class="panel-header">Rules</div>
      <form class="panel-body form" novalidate @submit.prevent="save">
        <div class="field">
          <label for="zs-severity">Fails from</label>
          <Select
            v-model="draft.minSeverity"
            input-id="zs-severity"
            :options="severityOptions"
            option-label="label"
            option-value="value"
            :disabled="!canWrite"
          />
          <small class="muted">A problem of this severity or worse fails the sensor (490).</small>
        </div>
        <label class="switch"
          ><ToggleSwitch v-model="draft.countAcknowledged" :disabled="!canWrite" /> Acknowledged problems count</label
        >
        <label class="switch"
          ><ToggleSwitch v-model="draft.countSuppressed" :disabled="!canWrite" /> Suppressed problems count (in
          maintenance)</label
        >
        <label class="switch"
          ><ToggleSwitch v-model="draft.unavailableFails" :disabled="!canWrite" /> An unavailable interface fails it
          (492)</label
        >
        <div v-if="canWrite" class="actions">
          <Button type="submit" label="Save" :disabled="!changed" />
          <Button label="Detach…" icon="pi pi-times" severity="danger" text @click="detach" />
        </div>
      </form>
    </section>
  </div>
</template>

<style scoped>
.zabbix-tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.column {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  min-width: 0;
}

.count {
  font-weight: 400;
}

.none {
  margin: 0 0 var(--pnmt-space-2);
}

.interfaces {
  margin: var(--pnmt-space-2) 0;
}

.links {
  display: flex;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
  margin: var(--pnmt-space-2) 0;
}

.switch {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.actions {
  display: flex;
  gap: var(--pnmt-space-2);
}

.error {
  color: var(--pnmt-fail);
}

.small {
  font-size: 0.85em;
}
</style>
