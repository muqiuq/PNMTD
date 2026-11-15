<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import { routerLocations, routersToSwitch } from './accessAdmin'
import { useAccessRouters } from './usePickers'

/**
 * The routers' switches (docs/cookie-access-plan.md §4.2): one row per router with its location, and all on, all off, or
 * all of a location at once. A switch takes effect at once and is in the audit log; it isn't part of the versioned
 * settings. Off, a router's logins are still read and shown on its Access tab, and nothing else happens.
 */
const session = useSession()
const routers = useAccessRouters()
const list = computed(() => routers.query.data.value ?? [])
const locations = computed(() => routerLocations(list.value))
const location = ref<string | null>(null)
const canSee = computed(() => session.can('Monitoring', 'View'))
const { done, ask, busy } = useActions()

async function setWatch(ids: string[], watch: boolean, what: string) {
  if (!ids.length) return
  if (!watch) {
    const sure = await ask(
      `Stop watching ${what}?`,
      'Their logins are still read and shown, but they open no case, send no notice and ask nobody. Each change is in the audit log.',
      'Stop watching',
    )
    if (!sure) return
  }
  await done(() => api.cookie.switchAccessRouters(ids, watch), {
    success: watch ? 'Cookie watches their logins' : "Cookie doesn't watch their logins",
    failure: 'Not switched',
    invalidate: [keys.cookie],
  })
}

const count = (ids: string[]) => (ids.length === 1 ? '1 router' : `${ids.length} routers`)
const locationName = computed(() => locations.value.find((l) => l.value === location.value)?.label ?? '')
function switchAll(watch: boolean) {
  const ids = routersToSwitch(list.value, null, watch)
  void setWatch(ids, watch, count(ids))
}
function switchLocation(watch: boolean) {
  if (location.value === null) return
  const ids = routersToSwitch(list.value, location.value, watch)
  void setWatch(ids, watch, `${count(ids)} of ${locationName.value}`)
}
const one = (r: S['AccessRouterItem'], watch: boolean) => setWatch([r.sensorId], watch, r.hostName)
const unwatched = computed(() => list.value.filter((r) => !r.watch).length)
</script>

<template>
  <section class="panel" aria-labelledby="access-routers-title">
    <div id="access-routers-title" class="panel-header">
      Routers
      <Tag
        v-if="list.length"
        :value="unwatched ? `${unwatched} not watched` : 'all watched'"
        :severity="unwatched ? 'warn' : 'success'"
      />
    </div>
    <div class="panel-body body">
      <p class="muted small intro">
        Whether Cookie watches a router's logins, set per router; the same switch is on each router's Access tab.
        Switched at once, and in the audit log: not with Save. A new router gets Watch new routers.
      </p>
      <ErrorState
        v-if="routers.query.isError.value && !routers.query.data.value"
        :error="routers.query.error.value"
        what="the routers"
        @retry="routers.query.refetch()"
      />
      <template v-else>
        <div class="toolbar">
          <Button
            label="All on"
            icon="pi pi-check"
            size="small"
            severity="secondary"
            outlined
            :disabled="busy || !routersToSwitch(list, null, true).length"
            @click="switchAll(true)"
          />
          <Button
            label="All off"
            icon="pi pi-times"
            size="small"
            severity="secondary"
            outlined
            :disabled="busy || !routersToSwitch(list, null, false).length"
            @click="switchAll(false)"
          />
          <span class="divider" aria-hidden="true" />
          <Select
            v-model="location"
            :options="locations"
            option-label="label"
            option-value="value"
            filter
            show-clear
            size="small"
            placeholder="All of a location…"
            aria-label="All of a location"
            class="location"
          />
          <Button
            label="On"
            size="small"
            severity="secondary"
            outlined
            :disabled="busy || location === null || !routersToSwitch(list, location, true).length"
            @click="switchLocation(true)"
          />
          <Button
            label="Off"
            size="small"
            severity="secondary"
            outlined
            :disabled="busy || location === null || !routersToSwitch(list, location, false).length"
            @click="switchLocation(false)"
          />
        </div>
        <p v-if="routers.query.data.value && !list.length" class="muted none">No router yet.</p>
        <DataTable
          v-else
          :value="list"
          :loading="routers.query.isPending.value"
          size="small"
          data-key="sensorId"
          scrollable
          scroll-height="24rem"
          class="table"
        >
          <Column header="Router">
            <template #body="{ data }">
              <RouterLink v-if="canSee" :to="{ name: 'sensor', params: { id: data.sensorId, tab: 'access' } }">{{
                data.hostName
              }}</RouterLink>
              <span v-else>{{ data.hostName }}</span>
              <span class="muted small"> · {{ data.name }}</span>
            </template>
          </Column>
          <Column header="Location">
            <template #body="{ data }">
              <span v-if="data.locationName">{{ data.locationName }}</span>
              <span v-else class="muted">–</span>
            </template>
          </Column>
          <Column header="Log first read" style="width: 9rem">
            <template #body="{ data }">
              <RelativeTime v-if="data.firstReadAt" :value="data.firstReadAt" />
              <span v-else class="muted">not yet</span>
              <Tag v-if="data.gap" value="lines may be missing" severity="warn" class="tag" />
            </template>
          </Column>
          <Column header="Watched" style="width: 6rem">
            <template #body="{ data }">
              <ToggleSwitch
                :model-value="data.watch"
                :disabled="busy"
                :aria-label="`Cookie watches the logins of ${data.hostName}`"
                @update:model-value="(v: boolean) => one(data, v)"
              />
            </template>
          </Column>
        </DataTable>
      </template>
    </div>
  </section>
</template>

<style scoped>
.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.intro,
.none {
  margin: 0;
}

.toolbar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.divider {
  width: 1px;
  align-self: stretch;
  background: var(--pnmt-border);
}

.location {
  min-width: 14rem;
}

.table {
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  overflow: hidden;
}

.tag {
  margin-left: var(--pnmt-space-1);
}

.small {
  font-size: 0.857rem;
}
</style>
