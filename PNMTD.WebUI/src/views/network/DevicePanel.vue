<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import StatusBadge, { type Status } from '@/components/StatusBadge.vue'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import ConnectionDialog from './ConnectionDialog.vue'
import {
  attachedLabel,
  confidenceSeverity,
  endLabel,
  kindLabel,
  matchLabel,
  nodeGlyph,
  portsLabel,
  scanLabel,
} from './networkFormat'

/**
 * The side panel (docs/network-map-plan.md §8.3): a router (its connections, ports from the scan, last scan), a host (its
 * adapters and interfaces), or a connection (its ends, type, notes, scan evidence). It fills the drawer: a header with
 * the name and the close button, the actions below it, then the sections, which scroll. From here: the router's or
 * host's page, the tabs filtered to it, Add connection (Edit), Scan this router (Commands: Run).
 */
const props = defineProps<{ selection: { kind: 'node' | 'edge'; id: string }; graph: S['NetworkGraph'] }>()
const emit = defineEmits<{ select: [id: string]; close: [] }>()
const session = useSession()
const { run, ask, done } = useActions()

const node = computed(() =>
  props.selection.kind === 'node' ? props.graph.nodes.find((n) => n.id === props.selection.id) : undefined,
)
const edge = computed(() =>
  props.selection.kind === 'edge' ? props.graph.edges.find((e) => e.id === props.selection.id) : undefined,
)
const device = useQuery({
  queryKey: computed(() => [...keys.network, 'device', node.value?.id ?? '']),
  queryFn: () => api.network.device(node.value!.kind, node.value!.sensorId ?? node.value!.hostId),
  enabled: computed(() => !!node.value),
})
const connection = useQuery({
  queryKey: computed(() => [...keys.network, 'connection', edge.value?.id ?? '']),
  queryFn: () => api.network.connection(edge.value!.id),
  enabled: computed(() => !!edge.value && !edge.value.suggestion),
})
const d = computed(() => device.data.value)
const c = computed(() => connection.data.value)
const adding = ref(false)
const editing = ref(false)

const status = (state: string): Status =>
  (['Ok', 'Failing', 'NoData', 'Ignored', 'Disabled'] as const).find((s) => s === state) ?? 'NoData'
const nodeById = (id: string) => props.graph.nodes.find((n) => n.id === id)
const canEdit = computed(() => session.can('NetworkMap', 'Edit'))
const canScan = computed(() => session.can('Commands', 'Run') || session.can('NetworkMap', 'Manage'))
const deviceId = computed(() => d.value?.sensorId ?? d.value?.hostId)
/** A connection's two ends, as the map has them. */
const ends = computed(() =>
  edge.value
    ? [
        { id: edge.value.source, port: edge.value.sourcePort, node: nodeById(edge.value.source) },
        { id: edge.value.target, port: edge.value.targetPort, node: nodeById(edge.value.target) },
      ]
    : [],
)

async function scan() {
  if (!d.value?.sensorId) return
  await run(() => api.network.startScan({ scope: 'Router', sensorId: d.value!.sensorId! }), {
    success: `${d.value.name} is queued for a scan`,
    failure: 'Not started',
    invalidate: [keys.network],
  })
}

/** The router back to automatic: the map lays it out again (docs/network-map-positions-plan.md §2). */
async function unpin() {
  if (!d.value?.sensorId) return
  await done(() => api.network.clearPosition(d.value!.sensorId!), {
    success: `${d.value.name} is placed automatically again`,
    invalidate: [keys.network],
  })
}

async function remove() {
  if (!c.value) return
  if (
    !(await ask(
      'Delete the connection?',
      `${endLabel(c.value.a)} ↔ ${endLabel(c.value.b)} goes from the map. The scans may suggest it again.`,
    ))
  )
    return
  await done(() => api.network.deleteConnection(c.value!.id), {
    success: 'Connection deleted',
    invalidate: [keys.network],
  })
}

async function accept() {
  if (!edge.value) return
  await run(() => api.network.accept(edge.value!.id), {
    success: 'Made a connection',
    failure: 'Not accepted',
    invalidate: [keys.network],
  })
}

function mine(e: S['NetworkEndItem']): boolean {
  return d.value!.kind === 'Router' ? e.sensorId === d.value!.sensorId : e.hostId === d.value!.hostId && !e.sensorId
}
function far(item: S['NetworkConnectionItem']): S['NetworkEndItem'] {
  return d.value && mine(item.a) ? item.b : item.a
}
function near(item: S['NetworkConnectionItem']): S['NetworkEndItem'] {
  return far(item) === item.a ? item.b : item.a
}
function nodeOf(end: S['NetworkEndItem']): string {
  return end.kind === 'Router' && end.sensorId ? `router:${end.sensorId}` : `host:${end.hostId}`
}
/** The far end as the map draws it: its state if it is on the map, its glyph. */
function farLook(end: S['NetworkEndItem']) {
  const n = nodeById(nodeOf(end))
  return { state: n?.state ?? 'None', glyph: nodeGlyph(end.kind, n?.os) }
}
function portLink(p: S['NetworkPortItem']): string {
  if (p.connectedTo) return p.connectedTo
  return p.role === 'ToRouter' ? 'a router' : ''
}
</script>

<template>
  <div class="device-panel">
    <!-- a router or a host -->
    <template v-if="node">
      <header class="head">
        <span class="avatar" :class="[`state-${node.state}`, { router: node.kind === 'Router' }]" aria-hidden="true">
          {{ nodeGlyph(node.kind, node.os) }}
        </span>
        <div class="title">
          <h2>{{ node.name }}</h2>
          <div class="sub">
            <StatusBadge :status="status(node.state)" />
            <span class="muted">· {{ kindLabel(node.kind, node.os) }}</span>
          </div>
        </div>
        <Button icon="pi pi-times" text rounded severity="secondary" aria-label="Close" @click="emit('close')" />
      </header>

      <div v-if="d" class="actions">
        <Button v-if="canEdit" label="Add connection" icon="pi pi-plus" size="small" @click="adding = true" />
        <Button
          v-if="d.kind === 'Router' && canScan"
          label="Scan"
          icon="pi pi-sync"
          size="small"
          severity="secondary"
          aria-label="Scan this router"
          @click="scan"
        />
        <RouterLink
          v-if="d.kind === 'Router' && d.sensorId"
          :to="{ name: 'sensor', params: { id: d.sensorId } }"
          class="button-link"
        >
          <Button label="Open router" icon="pi pi-external-link" size="small" severity="secondary" text />
        </RouterLink>
        <RouterLink v-else :to="{ name: 'host', params: { id: d.hostId } }" class="button-link">
          <Button label="Open host" icon="pi pi-external-link" size="small" severity="secondary" text />
        </RouterLink>
      </div>

      <div class="body">
        <p v-if="device.isError.value && !d" class="warn">The details could not be loaded.</p>
        <p v-else-if="!d" class="muted">Loading…</p>
        <template v-else>
          <RouterLink
            v-if="d.openSuggestions"
            class="callout"
            :to="{ name: 'network-map', query: { tab: 'suggestions', device: deviceId } }"
          >
            <i class="pi pi-lightbulb" aria-hidden="true" />
            <span
              >{{ d.openSuggestions }} open suggestion{{ d.openSuggestions === 1 ? '' : 's' }} for this
              {{ d.kind === 'Router' ? 'router' : 'host' }}</span
            >
            <i class="pi pi-angle-right" aria-hidden="true" />
          </RouterLink>

          <section>
            <h3>Details</h3>
            <dl class="facts">
              <dt>Location</dt>
              <dd :class="{ muted: !d.locationPath }">{{ d.locationPath ?? 'No location' }}</dd>
              <template v-if="d.kind === 'Router'">
                <dt>Host</dt>
                <dd>
                  <RouterLink :to="{ name: 'host', params: { id: d.hostId } }">{{ d.hostName }}</RouterLink>
                </dd>
                <dt>RouterOS</dt>
                <dd>{{ [d.version, d.model].filter(Boolean).join(' · ') || '–' }}</dd>
                <dt>Last scan</dt>
                <dd>
                  <RelativeTime v-if="d.scan?.scannedAt" :value="d.scan.scannedAt" />
                  <span v-else class="muted">Never</span>
                  <div v-if="d.scan?.state && d.scan.state !== 'Succeeded'" class="warn small">
                    {{ d.scan.state }}{{ d.scan.error ? `: ${d.scan.error}` : '' }}
                  </div>
                </dd>
                <dt>Position</dt>
                <dd>
                  <template v-if="d.position">
                    <span>Saved</span
                    ><span class="muted small">
                      · {{ d.position.updatedByName ?? 'someone' }}, <RelativeTime :value="d.position.updatedAt"
                    /></span>
                    <Button
                      v-if="canEdit"
                      label="Back to automatic"
                      size="small"
                      text
                      class="inline-action"
                      @click="unpin"
                    />
                  </template>
                  <span v-else class="muted">Automatic</span>
                </dd>
              </template>
              <template v-if="d.stateMessage">
                <dt>State</dt>
                <dd class="small">{{ d.stateMessage }}</dd>
              </template>
            </dl>
          </section>

          <section>
            <div class="section-head">
              <h3>
                Connections <span class="count">{{ d.connections.length }}</span>
              </h3>
              <RouterLink
                v-if="d.connections.length"
                class="more"
                :to="{ name: 'network-map', query: { tab: 'connections', device: deviceId } }"
                >Show in table</RouterLink
              >
            </div>
            <p v-if="!d.connections.length" class="muted small">
              None yet.<template v-if="canEdit"> Add one, or start a scan for suggestions.</template>
            </p>
            <ul v-else class="rows">
              <li v-for="item in d.connections" :key="item.id">
                <button type="button" class="row" @click="emit('select', nodeOf(far(item)))">
                  <span class="avatar mini" :class="`state-${farLook(far(item)).state}`" aria-hidden="true">
                    {{ farLook(far(item)).glyph }}
                  </span>
                  <span class="row-text">
                    <span class="row-name">{{ far(item).name }}</span>
                    <span class="row-meta">
                      <span class="chip">{{ item.typeLabel }}</span>
                      <span v-if="item.scan === 'Unconfirmed'" class="chip warn">no longer seen</span>
                      <span v-if="portsLabel(near(item).interface, far(item).interface)" class="mono">{{
                        portsLabel(near(item).interface, far(item).interface)
                      }}</span>
                    </span>
                  </span>
                  <i class="pi pi-angle-right chevron" aria-hidden="true" />
                </button>
              </li>
            </ul>
            <p v-if="node.hiddenConnections" class="muted small">
              +{{ node.hiddenConnections }} to devices the filters hide.
            </p>
          </section>

          <section v-if="d.ports.length">
            <h3>
              Ports <span class="count">{{ d.ports.length }}</span>
            </h3>
            <div class="table-wrap">
              <table class="ports">
                <thead>
                  <tr>
                    <th scope="col">Port</th>
                    <th scope="col">Type</th>
                    <th scope="col" class="num">MACs</th>
                    <th scope="col">Connected to</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="p in d.ports" :key="p.name" :class="{ down: !p.running }">
                    <td>
                      <span
                        class="dot"
                        :class="{ up: p.running }"
                        :title="p.disabled ? 'disabled' : p.running ? 'running' : 'not running'"
                        aria-hidden="true"
                      /><span class="mono">{{ p.name }}</span>
                    </td>
                    <td>{{ p.detectedTypeLabel ?? (p.rateMbps ? `${p.rateMbps} Mbit/s` : (p.type ?? '–')) }}</td>
                    <td class="num">{{ p.macCount || '' }}</td>
                    <td>
                      <Tag v-if="!p.connectedTo && p.suggestionId" value="suggestion" severity="info" />
                      <span v-else :class="{ muted: !p.connectedTo }">{{ portLink(p) }}</span>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          </section>

          <section v-if="d.adapters.length">
            <h3>
              Adapters <span class="count">{{ d.adapters.length }}</span>
            </h3>
            <ul class="rows plain">
              <li v-for="a in d.adapters" :key="a.adapter + a.mac">
                <span class="row-name">{{ a.adapter }}</span>
                <span class="row-meta dotted">
                  <span class="mono">{{ a.mac }}</span>
                  <span v-if="a.speedBps">{{ Math.round(a.speedBps / 1e6) }} Mbit/s</span>
                </span>
              </li>
            </ul>
          </section>

          <section v-if="d.interfaces?.length">
            <div class="section-head">
              <h3>
                Interfaces seen <span class="count">{{ d.interfacesSeen }}</span>
              </h3>
              <RouterLink
                v-if="session.can('NetworkMap', 'Details')"
                class="more"
                :to="{
                  name: 'network-map',
                  query: { tab: 'interfaces', ...(d.kind === 'Router' ? { router: d.sensorId } : { host: d.hostId }) },
                }"
                >Show all</RouterLink
              >
            </div>
            <ul class="rows plain">
              <li v-for="i in d.interfaces.slice(0, 12)" :key="i.mac">
                <span class="mono">{{ i.mac }}</span>
                <span class="row-meta">{{
                  d.kind === 'Router' ? [i.attachedPort, matchLabel(i)].filter(Boolean).join(' · ') : attachedLabel(i)
                }}</span>
              </li>
            </ul>
          </section>
        </template>
      </div>
      <ConnectionDialog
        v-if="d"
        v-model:visible="adding"
        :start="{ kind: d.kind, id: d.sensorId ?? d.hostId, name: d.name }"
      />
    </template>

    <!-- a connection or a suggestion -->
    <template v-else-if="edge">
      <header class="head">
        <span class="avatar link-avatar" aria-hidden="true"><i class="pi pi-arrow-right-arrow-left" /></span>
        <div class="title">
          <h2>{{ edge.suggestion ? 'Suggestion' : 'Connection' }}</h2>
          <div class="sub">
            <span class="muted">{{ edge.typeLabel ?? 'No type proposed' }}</span>
            <Tag
              v-if="edge.confidence"
              :value="`${edge.confidence} confidence`"
              :severity="confidenceSeverity(edge.confidence)"
            />
          </div>
        </div>
        <Button icon="pi pi-times" text rounded severity="secondary" aria-label="Close" @click="emit('close')" />
      </header>

      <div v-if="(edge.suggestion || c) && canEdit" class="actions">
        <template v-if="edge.suggestion">
          <Button label="Make connection" icon="pi pi-check" size="small" :disabled="!edge.type" @click="accept" />
          <RouterLink :to="{ name: 'network-map', query: { tab: 'suggestions' } }" class="button-link">
            <Button label="Review in Suggestions" size="small" severity="secondary" text />
          </RouterLink>
        </template>
        <template v-else-if="c">
          <Button label="Edit" icon="pi pi-pencil" size="small" severity="secondary" @click="editing = true" />
          <Button label="Delete" icon="pi pi-trash" size="small" severity="danger" text @click="remove" />
        </template>
      </div>

      <div class="body">
        <section>
          <h3>Ends</h3>
          <ol class="ends" :class="{ virtual: !edge.physical, suggestion: edge.suggestion }">
            <li v-for="(end, index) in ends" :key="end.id" class="end" :class="{ last: index === ends.length - 1 }">
              <span class="avatar mini" :class="`state-${end.node?.state ?? 'None'}`" aria-hidden="true">{{
                end.node ? nodeGlyph(end.node.kind, end.node.os) : ''
              }}</span>
              <span class="row-text">
                <button type="button" class="link row-name" @click="emit('select', end.id)">
                  {{ end.node?.name ?? '(not on the map)' }}
                </button>
                <span v-if="end.port" class="row-meta mono">{{ end.port }}</span>
              </span>
            </li>
          </ol>
        </section>

        <section>
          <h3>Details</h3>
          <dl class="facts">
            <dt>Type</dt>
            <dd>{{ edge.typeLabel ?? 'None proposed' }}</dd>
            <dt>Kind</dt>
            <dd>{{ edge.physical ? 'Physical' : 'Virtual' }}</dd>
            <template v-if="c">
              <dt>Scan</dt>
              <dd :class="{ warn: c.scan === 'Unconfirmed' }">
                {{ scanLabel(c.scan)
                }}<template v-if="c.confirmedAt"> · <RelativeTime :value="c.confirmedAt" /></template>
              </dd>
              <dt>Made</dt>
              <dd>
                {{ c.source === 'Suggestion' ? 'From a suggestion' : 'By hand'
                }}<template v-if="c.createdByName">, by {{ c.createdByName }}</template>
              </dd>
            </template>
          </dl>
        </section>

        <section v-if="c">
          <h3>Notes</h3>
          <p class="notes" :class="{ muted: !c.notes }">{{ c.notes ?? 'No notes.' }}</p>
        </section>
      </div>
      <ConnectionDialog v-if="c" v-model:visible="editing" :connection="c" />
    </template>
  </div>
</template>

<style scoped>
.device-panel {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}

/* The header: the device as the map draws it, its name, state and kind; the close button. */
.head {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-3);
  padding: var(--pnmt-space-4) var(--pnmt-space-3) var(--pnmt-space-3) var(--pnmt-space-4);
}

.title {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
  padding-top: 2px;
}

h2 {
  font-size: 1.1429rem;
  line-height: 1.3;
  overflow-wrap: anywhere;
}

.sub {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
  font-size: 0.929rem;
}

.avatar {
  flex: none;
  display: inline-grid;
  place-items: center;
  width: 2.75rem;
  height: 2.75rem;
  border-radius: 50%;
  border: 2px solid var(--pnmt-none);
  background: var(--pnmt-none-soft);
  color: var(--pnmt-text);
  font-weight: 700;
  font-size: 1.071rem;
}

.avatar.router {
  border-width: 3px;
}

.avatar.state-Ok {
  border-color: var(--pnmt-ok);
  background: var(--pnmt-ok-soft);
}

.avatar.state-Failing {
  border-color: var(--pnmt-fail);
  background: var(--pnmt-fail-soft);
}

.avatar.link-avatar {
  border-color: var(--p-primary-color);
  background: color-mix(in srgb, var(--p-primary-color) 10%, transparent);
  color: var(--p-primary-color);
}

.avatar.mini {
  width: 1.75rem;
  height: 1.75rem;
  border-width: 1.5px;
  font-size: 0.786rem;
}

.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: 0 var(--pnmt-space-4) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
}

.head:has(+ .body) {
  border-bottom: 1px solid var(--pnmt-border);
}

.button-link:hover {
  text-decoration: none;
}

/* The sections scroll below the header and the actions. */
.body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-6);
  padding: var(--pnmt-space-4) var(--pnmt-space-4) calc(var(--pnmt-space-6) + env(safe-area-inset-bottom, 0px));
}

section {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.section-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: var(--pnmt-space-2);
}

h3 {
  font-size: 0.786rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pnmt-muted);
}

.count {
  margin-left: 0.25rem;
  font-weight: 500;
}

.more {
  font-size: 0.857rem;
}

.callout {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-radius: var(--p-border-radius-md);
  background: color-mix(in srgb, var(--p-primary-color) 8%, transparent);
  color: var(--pnmt-text);
}

.callout:hover {
  text-decoration: none;
  background: color-mix(in srgb, var(--p-primary-color) 14%, transparent);
}

.callout .pi-lightbulb {
  color: var(--p-primary-color);
}

.callout span {
  flex: 1;
}

/* Rows: a connection to open, an adapter, an interface. */
.rows {
  list-style: none;
  margin: 0 calc(-1 * var(--pnmt-space-2));
  padding: 0;
  display: flex;
  flex-direction: column;
}

.rows.plain {
  margin: 0;
  gap: var(--pnmt-space-2);
}

.rows.plain li {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
  width: 100%;
  min-height: 2.75rem;
  padding: var(--pnmt-space-2);
  border: 0;
  border-radius: var(--p-border-radius-md);
  background: none;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.row:hover {
  background: var(--p-content-hover-background);
}

.row:focus-visible {
  outline: 2px solid var(--p-primary-color);
  outline-offset: -2px;
}

.row-text {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.row-name {
  font-weight: 500;
  overflow-wrap: anywhere;
}

.row-meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
  color: var(--pnmt-muted);
  font-size: 0.857rem;
  overflow-wrap: anywhere;
}

.row-meta.dotted > span + span::before {
  content: '·';
  margin-right: var(--pnmt-space-2);
  color: var(--pnmt-muted);
}

/* The type of a connection: a small pill, as in the tables. */
.chip {
  padding: 0 0.45rem;
  border: 1px solid var(--pnmt-border);
  border-radius: 999px;
  color: var(--pnmt-text);
  font-size: 0.786rem;
  line-height: 1.5;
  white-space: nowrap;
}

.chip.warn {
  border-color: currentColor;
  color: var(--pnmt-warn);
}

.chevron {
  color: var(--pnmt-muted);
}

/* A connection's two ends, joined by a line drawn as on the map: dashed virtual, dotted a suggestion. */
.ends {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
}

.end {
  position: relative;
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-3);
  padding-bottom: var(--pnmt-space-4);
}

.end.last {
  padding-bottom: 0;
}

.end:not(.last)::after {
  content: '';
  position: absolute;
  left: calc(0.875rem - 1px);
  top: 1.75rem;
  bottom: 0;
  border-left: 2px solid var(--pnmt-muted);
}

.ends.virtual .end:not(.last)::after {
  border-left-style: dashed;
}

.ends.suggestion .end:not(.last)::after {
  border-left-style: dotted;
}

.end .row-text {
  padding-top: 0.2rem;
}

.link {
  background: none;
  border: 0;
  padding: 0;
  color: var(--p-primary-color);
  cursor: pointer;
  text-align: left;
  font: inherit;
}

.link:hover {
  text-decoration: underline;
}

/* The ports from the last scan. */
.table-wrap {
  overflow-x: auto;
}

.ports {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.857rem;
}

.ports th {
  text-align: left;
  font-weight: 500;
  color: var(--pnmt-muted);
  padding: 0 var(--pnmt-space-2) var(--pnmt-space-1) 0;
  border-bottom: 1px solid var(--pnmt-border);
  white-space: nowrap;
}

.ports td {
  padding: 0.35rem var(--pnmt-space-2) 0.35rem 0;
  border-bottom: 1px solid var(--pnmt-border);
  vertical-align: middle;
}

.ports td:first-child,
.ports td:nth-child(2) {
  white-space: nowrap;
}

.ports tr.down td {
  color: var(--pnmt-muted);
}

.ports .num {
  text-align: right;
  padding-right: var(--pnmt-space-3);
}

.dot {
  display: inline-block;
  width: 0.5rem;
  height: 0.5rem;
  margin-right: 0.4rem;
  border-radius: 50%;
  background: var(--pnmt-none);
  vertical-align: 0.05em;
}

.dot.up {
  background: var(--pnmt-ok);
}

.facts {
  row-gap: var(--pnmt-space-2);
  align-items: baseline;
}

.small {
  font-size: 0.857rem;
}

.warn {
  color: var(--pnmt-warn);
}

.inline-action {
  margin-left: var(--pnmt-space-1);
  padding-block: 0;
}

.notes {
  margin: 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
</style>
