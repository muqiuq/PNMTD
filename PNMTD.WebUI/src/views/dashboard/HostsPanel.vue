<script setup lang="ts">
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import type { MenuItem } from 'primevue/menuitem'
import { computed, ref } from 'vue'
import type { S } from '@/api/resources'
import MoreMenu from '@/components/MoreMenu.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import UptimeBar from '@/components/UptimeBar.vue'
import { formatPercent } from '@/format'
import { useMedia } from '@/composables/useMedia'
import { usePreferences } from '@/stores/preferences'
import LocationPicker from '@/views/locations/LocationPicker.vue'
import { relativePath, subtreeIds } from '@/views/locations/locationTree'
import { useLocations } from '@/views/locations/useLocations'

/**
 * Every host at a glance (§5.5): failing first, then by name; a filter and sorting; rows with a
 * 90-day bar that ends today, or dense tiles. Disabled hosts are counted and folded at the bottom.
 * On the dashboard, a filter by location, sub-locations included (docs/locations-knowledge-plan.md
 * §3.2). On a location's page (<code>base</code>), each host names the sub-location it belongs to,
 * and <code>rowMenu</code> gives each row its actions (§3.4).
 */
const props = withDefaults(
  defineProps<{
    hosts: S['DashboardHost'][]
    title?: string
    base?: { id: string; path: string } | null
    rowMenu?: ((host: S['DashboardHost']) => MenuItem[]) | null
  }>(),
  { title: 'Hosts', base: null, rowMenu: null },
)
const preferences = usePreferences()

const filter = ref('')
// The dashboard's location filter: null for all, 'none' for hosts without a location.
const location = ref<string | null>(null)
const locations = useLocations()
const withLocationFilter = computed(() => !props.base && (locations.data.value?.items.length ?? 0) > 0)
const inLocation = computed(() =>
  location.value && location.value !== 'none' ? subtreeIds(locations.data.value?.items ?? [], location.value) : null,
)
const where = (host: S['DashboardHost']) =>
  props.base && host.assignedLocation && host.assignedLocation.id !== props.base.id
    ? relativePath(host.assignedLocation.path, props.base.path)
    : ''

const sort = ref<'state' | 'name' | 'uptime'>('state')
const sorts = [
  { label: 'Failing first', value: 'state' },
  { label: 'Name', value: 'name' },
  { label: 'Uptime (lowest first)', value: 'uptime' },
]
const views = [
  { icon: 'pi pi-bars', value: 'rows', label: 'Rows' },
  { icon: 'pi pi-th-large', value: 'tiles', label: 'Tiles' },
]
const showDisabled = ref(false)
const phone = useMedia('(max-width: 599px)')
const view = computed({
  get: () => preferences.hostsView ?? (phone.value ? 'tiles' : 'rows'),
  set: (value) => (preferences.hostsView = value),
})

const stateRank: Record<string, number> = { Failing: 0, NoData: 1, Ok: 2, Disabled: 3 }

const matching = computed(() => {
  const text = filter.value.trim().toLowerCase()
  return props.hosts.filter(
    (h) =>
      (!text ||
        h.name.toLowerCase().includes(text) ||
        (h.location ?? '').toLowerCase().includes(text) ||
        (h.assignedLocation?.path ?? '').toLowerCase().includes(text)) &&
      (location.value !== 'none' || !h.assignedLocation) &&
      (!inLocation.value || (!!h.assignedLocation && inLocation.value.has(h.assignedLocation.id))),
  )
})

const sorted = (list: S['DashboardHost'][]) =>
  [...list].sort((a, b) => {
    if (sort.value === 'uptime') {
      return (a.uptime30Days ?? 2) - (b.uptime30Days ?? 2) || a.name.localeCompare(b.name)
    }
    if (sort.value === 'state') {
      return (stateRank[a.state] ?? 9) - (stateRank[b.state] ?? 9) || a.name.localeCompare(b.name)
    }
    return a.name.localeCompare(b.name)
  })

const enabled = computed(() => sorted(matching.value.filter((h) => h.enabled)))
const disabled = computed(() => sorted(matching.value.filter((h) => !h.enabled)))
</script>

<template>
  <section class="panel hosts-panel" aria-labelledby="hosts-title">
    <header class="panel-header">
      <span id="hosts-title">{{ title }}</span>
      <slot name="actions" />
      <InputText v-model="filter" size="small" placeholder="Filter…" class="filter" aria-label="Filter hosts" />
      <LocationPicker
        v-if="withLocationFilter"
        v-model="location"
        none-label="All locations"
        with-unassigned
        size="small"
        aria-label="Filter hosts by location"
        class="location"
      />
      <Select
        v-model="sort"
        :options="sorts"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Sort hosts"
        class="sort"
      />
      <SelectButton
        v-model="view"
        :options="views"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Host view"
      >
        <template #option="{ option }">
          <i :class="option.icon" :aria-label="option.label" :title="option.label" />
        </template>
      </SelectButton>
    </header>
    <div class="scroller">
      <div v-if="!enabled.length && !disabled.length" class="empty muted">No host matches.</div>
      <ul v-if="view === 'rows'" class="rows">
        <li v-for="host in enabled" :key="host.id" :class="{ 'with-menu': rowMenu }">
          <RouterLink
            :to="{ name: 'host', params: { id: host.id } }"
            class="row"
            :class="{ failing: host.state === 'Failing' }"
          >
            <StatusBadge
              :status="host.state"
              icon-only
              :label="host.state === 'Failing' ? `${host.failingCount} failing` : undefined"
            />
            <span class="name ellipsis" :title="host.assignedLocation?.path ?? host.location ?? ''"
              >{{ host.name
              }}<span v-if="where(host)" class="where muted"
                ><span class="sr-only">, </span>{{ where(host) }}</span
              ></span
            >
            <span class="percent">{{ formatPercent(host.uptime30Days) }}</span>
            <UptimeBar :values="host.days.values ?? []" :start="host.days.start" :height="14" class="bar" />
          </RouterLink>
          <MoreMenu v-if="rowMenu" :items="rowMenu(host)" :label="`More actions for ${host.name}`" />
        </li>
      </ul>
      <ul v-else class="tiles">
        <li v-for="host in enabled" :key="host.id">
          <RouterLink :to="{ name: 'host', params: { id: host.id } }" class="tile" :class="`state-${host.state}`">
            <span class="name ellipsis">{{ host.name }}</span>
            <span v-if="where(host)" class="where muted ellipsis"
              ><span class="sr-only">, </span>{{ where(host) }}</span
            >
            <span class="meta">
              <span>{{ formatPercent(host.uptime30Days) }}</span>
              <span v-if="host.failingCount" class="fail">{{ host.failingCount }} failing</span>
            </span>
          </RouterLink>
        </li>
      </ul>
      <div v-if="disabled.length" class="disabled">
        <button type="button" class="toggle muted" :aria-expanded="showDisabled" @click="showDisabled = !showDisabled">
          <i :class="showDisabled ? 'pi pi-angle-down' : 'pi pi-angle-right'" aria-hidden="true" />
          Disabled ({{ disabled.length }})
        </button>
        <ul v-if="showDisabled" class="rows muted-rows">
          <li v-for="host in disabled" :key="host.id" :class="{ 'with-menu': rowMenu }">
            <RouterLink :to="{ name: 'host', params: { id: host.id } }" class="row">
              <StatusBadge status="Disabled" icon-only />
              <span class="name ellipsis"
                >{{ host.name
                }}<span v-if="where(host)" class="where muted"
                  ><span class="sr-only">, </span>{{ where(host) }}</span
                ></span
              >
              <span class="percent">{{ formatPercent(host.uptime30Days) }}</span>
              <UptimeBar :values="host.days.values ?? []" :start="host.days.start" :height="14" class="bar" />
            </RouterLink>
            <MoreMenu v-if="rowMenu" :items="rowMenu(host)" :label="`More actions for ${host.name}`" />
          </li>
        </ul>
      </div>
    </div>
  </section>
</template>

<style scoped>
.hosts-panel {
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.panel-header {
  flex-wrap: wrap;
  /* Wrapped onto two lines, it keeps its height: min-height alone would let the list squeeze it. */
  flex-shrink: 0;
  padding-top: var(--pnmt-space-1);
  padding-bottom: var(--pnmt-space-1);
}

.filter {
  margin-left: auto;
  width: 9rem;
}

.sort {
  width: 10.5rem;
}

.location {
  width: 11rem;
  min-width: 0;
}

.with-menu {
  display: flex;
  align-items: center;
  padding-right: var(--pnmt-space-1);
}

.with-menu .row {
  flex: 1;
  min-width: 0;
}

.with-menu :deep(.p-button) {
  width: 1.75rem;
  height: 1.75rem;
}

.where {
  font-weight: 400;
  font-size: 0.857rem;
}

.row .where {
  margin-left: var(--pnmt-space-2);
}

/* Vue drops a leading space inside an element, so a screen reader read "fw-01Rack B4": a comma only it hears. */
.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
}

.scroller {
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
}

.empty {
  padding: var(--pnmt-space-3);
}

.rows,
.tiles {
  list-style: none;
  margin: 0;
  padding: var(--pnmt-space-1) 0;
}

.row {
  display: grid;
  grid-template-columns: 1.25rem minmax(5rem, 12rem) 4rem minmax(0, 1fr);
  align-items: center;
  gap: var(--pnmt-space-2);
  height: 26px;
  padding: 0 var(--pnmt-space-3);
  color: var(--pnmt-text);
}

.row:hover {
  background: var(--p-content-hover-background);
  text-decoration: none;
}

.row.failing .name {
  font-weight: 600;
}

.percent {
  text-align: right;
  font-variant-numeric: tabular-nums;
  font-size: 0.929rem;
}

.bar {
  min-width: 0;
}

.tiles {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(118px, 1fr));
  gap: var(--pnmt-space-1);
  padding: var(--pnmt-space-2);
}

.tile {
  display: flex;
  flex-direction: column;
  gap: 1px;
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  border-radius: var(--p-border-radius-sm);
  border-left: 4px solid var(--pnmt-ok);
  background: var(--pnmt-bg);
  color: var(--pnmt-text);
  font-size: 0.929rem;
}

.tile:hover {
  text-decoration: none;
  background: var(--p-content-hover-background);
}

.state-Failing {
  border-left-color: var(--pnmt-fail);
  background: var(--pnmt-fail-soft);
}

.state-NoData {
  border-left-color: var(--pnmt-none);
}

.tile .name {
  font-weight: 500;
}

.meta {
  display: flex;
  justify-content: space-between;
  gap: var(--pnmt-space-1);
  color: var(--pnmt-muted);
  font-size: 0.857rem;
}

.fail {
  color: var(--pnmt-fail);
  font-weight: 600;
}

.disabled {
  border-top: 1px solid var(--pnmt-border);
}

.toggle {
  all: unset;
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  cursor: pointer;
}

.muted-rows .row {
  opacity: 0.65;
}

@media (max-width: 599px) {
  .filter {
    flex: 1;
    width: auto;
  }

  .sort,
  .location {
    display: none;
  }

  .row {
    grid-template-columns: 1.25rem minmax(0, 1fr) 3.6rem 90px;
  }

  .tiles {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
</style>
