<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Drawer from 'primevue/drawer'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import type { MenuItem } from 'primevue/menuitem'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { onBeforeRouteLeave, onBeforeRouteUpdate, useRoute, useRouter } from 'vue-router'
import { api, keys } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import { pickerOptions } from '@/views/locations/locationTree'
import { useLocations } from '@/views/locations/useLocations'
import DevicePanel from './DevicePanel.vue'
import MapCanvas, { type Selection } from './MapCanvas.vue'
import { parseFocus } from './networkFormat'
import type { Point } from './networkLayout'

/**
 * The map (docs/network-map-plan.md §8.2): the routers by default; the hosts of chosen locations on request; each top-level
 * location a dashed circle. The filters are in the URL, so a view can be shared; ?focus= centres a node or opens a
 * connection.
 *
 * Saving positions (docs/network-map-positions-plan.md §2): the moves are kept here, across filters and refreshes. With
 * Edit, a bar offers Save and Discard; saving writes the moved routers and the shown routers that were never saved, so
 * nothing moves afterwards. Leaving the map with unsaved moves asks first.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const locations = useLocations()
const locationOptions = computed(() => pickerOptions(locations.data.value?.items ?? [], null))

const list = (v: unknown) => (typeof v === 'string' && v ? v.split(',') : [])
const routersMode = ref<'all' | 'locations' | 'problems'>(
  (['all', 'locations', 'problems'] as const).find((m) => m === route.query.routers) ?? 'all',
)
const routerLocations = ref<string[]>(list(route.query.rl))
const hostLocations = ref<string[]>(list(route.query.hl))
const onlyConnected = ref(route.query.unconnected !== '1')
const suggestions = ref(route.query.suggestions === '1')
const kind = ref<'all' | 'physical' | 'virtual'>(
  route.query.links === 'physical' ? 'physical' : route.query.links === 'virtual' ? 'virtual' : 'all',
)

watch([routersMode, routerLocations, hostLocations, onlyConnected, suggestions, kind], () => {
  const query: Record<string, string> = {}
  for (const [k, v] of Object.entries(route.query)) if (typeof v === 'string') query[k] = v
  const set = (k: string, v: string | null) => (v ? (query[k] = v) : delete query[k])
  set('routers', routersMode.value === 'all' ? null : routersMode.value)
  set('rl', routersMode.value === 'locations' && routerLocations.value.length ? routerLocations.value.join(',') : null)
  set('hl', hostLocations.value.length ? hostLocations.value.join(',') : null)
  set('unconnected', onlyConnected.value ? null : '1')
  set('suggestions', suggestions.value ? '1' : null)
  set('links', kind.value === 'all' ? null : kind.value)
  void router.replace({ query })
})

const graph = useQuery({
  queryKey: computed(() => [
    ...keys.network,
    'graph',
    routersMode.value,
    routerLocations.value,
    hostLocations.value,
    onlyConnected.value,
    suggestions.value,
    kind.value,
  ]),
  queryFn: () =>
    api.network.graph({
      routers: routersMode.value,
      routerLocationIds: routersMode.value === 'locations' ? routerLocations.value : undefined,
      hostLocationIds: hostLocations.value.length ? hostLocations.value : undefined,
      onlyConnected: onlyConnected.value,
      suggestions: suggestions.value,
      physical: kind.value === 'all' ? undefined : kind.value === 'physical',
    }),
  placeholderData: keepPreviousData,
  refetchInterval: 60_000,
})

const selected = ref<Selection>(null)
const focus = computed(() => {
  const f = parseFocus(route.query.focus)
  if (!f || f.kind === 'connection') return null
  const nodes = graph.data.value?.nodes ?? []
  // A host that carries a router is drawn as its router.
  if (f.kind === 'host' && !nodes.some((n) => n.id === `host:${f.id}`)) {
    const router = nodes.find((n) => n.kind === 'Router' && n.hostId === f.id)
    if (router) return router.id
  }
  return `${f.kind}:${f.id}`
})
// A host the filters don't show: its location's hosts are shown, with or without a connection.
watch(
  () => [route.query.focus, graph.data.value] as const,
  async () => {
    const f = parseFocus(route.query.focus)
    const nodes = graph.data.value?.nodes
    if (
      f?.kind !== 'host' ||
      !nodes ||
      nodes.some((n) => n.id === `host:${f.id}` || (n.kind === 'Router' && n.hostId === f.id))
    )
      return
    const host = await api.network.device('Host', f.id).catch(() => null)
    if (host?.locationId && !hostLocations.value.includes(host.locationId)) {
      hostLocations.value = [...hostLocations.value, host.locationId]
      onlyConnected.value = false
    }
  },
  { immediate: true },
)
watch(
  () => [route.query.focus, graph.data.value] as const,
  () => {
    const f = parseFocus(route.query.focus)
    if (f?.kind === 'connection' && graph.data.value?.edges.some((e) => e.id === f.id))
      selected.value = { kind: 'edge', id: f.id }
  },
  { immediate: true },
)
const open = computed({ get: () => selected.value !== null, set: (v) => !v && (selected.value = null) })
// The panel keeps the last selection while the drawer slides out.
const shown = ref<Selection>(null)
watch(selected, (s) => s && (shown.value = s), { immediate: true })
const search = ref<string | null>(null)
const searchFocus = ref<string | null>(null)
watch(search, (id) => {
  if (id) {
    searchFocus.value = id
    selected.value = { kind: 'node', id }
  }
})
const nodeOptions = computed(() => (graph.data.value?.nodes ?? []).map((n) => ({ value: n.id, label: n.name })))
const routerChoices = [
  { label: 'All routers', value: 'all' },
  { label: 'Routers of…', value: 'locations' },
  { label: 'Routers with a problem', value: 'problems' },
]
const kindChoices = [
  { label: 'All connections', value: 'all' },
  { label: 'Physical', value: 'physical' },
  { label: 'Virtual', value: 'virtual' },
]
const empty = computed(() => graph.data.value && !graph.data.value.nodes.length)

// --- moving and saving the routers ---------------------------------------------------------------------------------------

const { run, done, ask, busy } = useActions()
const canvas = ref<InstanceType<typeof MapCanvas> | null>(null)
const moved = ref(new Map<string, Point>())
const canEdit = computed(() => session.can('NetworkMap', 'Edit'))
const movedRouters = computed(() => [...moved.value.keys()].filter((id) => id.startsWith('router:')))
const dirty = computed(() => canEdit.value && movedRouters.value.length > 0)
const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`

/** What Save writes: the moved routers, and the shown routers without a saved position, where they are now. */
function toSave(): Map<string, Point> {
  const out = new Map<string, Point>()
  for (const id of movedRouters.value) out.set(id, moved.value.get(id)!)
  const now = canvas.value?.positions() ?? new Map<string, Point>()
  for (const n of graph.data.value?.nodes ?? []) {
    const p = now.get(n.id)
    if (n.kind === 'Router' && n.x == null && !out.has(n.id) && p) out.set(n.id, p)
  }
  return out
}
/** Shown routers without a saved position, and moved ones: whether "Save this layout" has anything to write. */
const toSaveCount = computed(
  () =>
    movedRouters.value.length +
    (graph.data.value?.nodes ?? []).filter((n) => n.kind === 'Router' && n.x == null && !moved.value.has(n.id)).length,
)
const round = (v: number) => Math.round(v * 100) / 100

async function save() {
  const points = toSave()
  if (!points.size) return
  const ok = await run(
    () =>
      api.network.savePositions(
        [...points].map(([id, p]) => ({ sensorId: id.slice('router:'.length), x: round(p.x), y: round(p.y) })),
      ),
    {
      success: `Saved the position${points.size === 1 ? '' : 's'} of ${plural(points.size, 'router')}`,
      failure: 'Positions not saved',
      invalidate: [keys.network],
    },
  )
  // The graph has the saved positions now: the routers' moves can go without anything jumping.
  if (ok) {
    const next = new Map(moved.value)
    for (const id of points.keys()) next.delete(id)
    moved.value = next
  }
}

function discard() {
  moved.value = new Map()
}

async function reset() {
  const count = graph.data.value?.savedPositions ?? 0
  const sure = await ask(
    'Back to automatic for all routers?',
    `The saved positions of ${plural(count, 'router')} are cleared, for everyone. The map lays every router out automatically again.`,
    'Back to automatic',
  )
  if (!sure) return
  const ok = await done(() => api.network.resetPositions(), {
    success: 'Every router is placed automatically again',
    invalidate: [keys.network],
  })
  if (ok) discard()
}

const layoutItems = computed<MenuItem[]>(() => [
  { label: 'Save this layout', icon: 'pi pi-save', disabled: !toSaveCount.value, command: () => void save() },
  {
    label: 'Back to automatic for all routers…',
    icon: 'pi pi-replay',
    disabled: !graph.data.value?.savedPositions,
    command: () => void reset(),
  },
])

// Leaving with unsaved moves: another page, another tab of this page, or the browser's reload.
async function mayLeave(): Promise<boolean> {
  if (!dirty.value) return true
  const leave = await ask('Leave without saving?', 'The routers you moved are not saved.', 'Leave')
  if (leave) discard()
  return leave
}
onBeforeRouteLeave(mayLeave)
onBeforeRouteUpdate((to) => ((typeof to.query.tab === 'string' ? to.query.tab : 'map') === 'map' ? true : mayLeave()))
function onBeforeUnload(e: BeforeUnloadEvent) {
  if (dirty.value) e.preventDefault()
}
onMounted(() => window.addEventListener('beforeunload', onBeforeUnload))
onBeforeUnmount(() => window.removeEventListener('beforeunload', onBeforeUnload))
</script>

<template>
  <div class="tab">
    <div class="filters">
      <Select
        v-model="routersMode"
        :options="routerChoices"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Routers"
      />
      <MultiSelect
        v-if="routersMode === 'locations'"
        v-model="routerLocations"
        :options="locationOptions"
        option-label="label"
        option-value="value"
        filter
        placeholder="Locations"
        size="small"
        display="chip"
        class="locations"
        aria-label="The routers' locations"
      />
      <MultiSelect
        v-model="hostLocations"
        :options="locationOptions"
        option-label="label"
        option-value="value"
        filter
        placeholder="Hosts of…"
        size="small"
        display="chip"
        class="locations"
        aria-label="Show the hosts of these locations"
      />
      <label class="check">
        <Checkbox v-model="onlyConnected" binary input-id="only-connected" />
        <span>Only hosts with a connection</span>
      </label>
      <Select
        v-model="kind"
        :options="kindChoices"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Connections"
      />
      <label class="check">
        <Checkbox v-model="suggestions" binary input-id="show-suggestions" />
        <span>Show suggestions</span>
      </label>
      <span class="spacer" />
      <Select
        v-model="search"
        :options="nodeOptions"
        option-label="label"
        option-value="value"
        filter
        show-clear
        placeholder="Find a device"
        size="small"
        class="search"
        aria-label="Find a device on the map"
      />
    </div>

    <ErrorState
      v-if="graph.isError.value && !graph.data.value"
      :error="graph.error.value"
      what="the network map"
      @retry="graph.refetch()"
    />
    <EmptyState
      v-else-if="empty"
      title="No connections yet"
      text="Start a scan: PNMT reads the routers' neighbors, MAC tables, ARP and DHCP, and suggests connections. Hosts of locations come with the filter above."
    >
      <RouterLink v-if="session.can('NetworkMap', 'Manage')" :to="{ name: 'network-map', query: { tab: 'scans' } }">
        <Button label="Start a scan" icon="pi pi-sync" size="small" />
      </RouterLink>
    </EmptyState>
    <MapCanvas
      v-else-if="graph.data.value"
      ref="canvas"
      v-model:selected="selected"
      v-model:moved="moved"
      :graph="graph.data.value"
      :focus="searchFocus ?? focus"
      :editable="canEdit"
    >
      <template #bar>
        <div v-if="dirty" class="save-bar" role="status">
          <span>{{ plural(movedRouters.length, 'router') }} moved</span>
          <Button label="Discard" size="small" severity="secondary" text :disabled="busy" @click="discard" />
          <Button label="Save positions" icon="pi pi-save" size="small" :loading="busy" @click="save" />
        </div>
      </template>
      <template #controls>
        <MoreMenu v-if="canEdit" :items="layoutItems" label="Layout" />
      </template>
    </MapCanvas>

    <Drawer
      v-model:visible="open"
      position="right"
      :modal="false"
      :dismissable="false"
      class="network-drawer"
      aria-label="Details"
    >
      <template #container="{ closeCallback }">
        <DevicePanel
          v-if="shown && graph.data.value"
          :selection="shown"
          :graph="graph.data.value"
          @select="(id: string) => ((selected = { kind: 'node', id }), (searchFocus = id))"
          @close="closeCallback"
        />
      </template>
    </Drawer>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.filters {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.locations {
  min-width: 12rem;
  max-width: 24rem;
}

.search {
  width: 14rem;
}

.save-bar {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-1) var(--pnmt-space-1) var(--pnmt-space-1) var(--pnmt-space-3);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-panel);
  box-shadow: 0 2px 10px rgb(0 0 0 / 12%);
  white-space: nowrap;
}

.save-bar span {
  font-weight: 500;
}

.check {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  font-size: 0.929rem;
}

.spacer {
  flex: 1;
}
</style>

<style>
/* PrimeVue's own rule (.p-drawer-right .p-drawer: 20rem) comes later and weighs as much as a two-class one: one more. */
.p-drawer-mask .p-drawer.network-drawer {
  width: min(30rem, 100vw);
}

/* A phone: the whole width; the header's close button goes back to the map. */
@media (max-width: 599px) {
  .p-drawer-mask .p-drawer.network-drawer {
    width: 100vw;
  }
}
</style>
