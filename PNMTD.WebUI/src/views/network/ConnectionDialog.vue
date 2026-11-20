<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import { endLabel, mediumLabels } from './networkFormat'

/**
 * A connection (docs/network-map-plan.md §8.4): new (each end a router or host from a search, its port from the scan's ports
 * or the server's adapters, or free text; the type grouped by medium, the router port's proposal preselected), edited (ports,
 * type, notes), or a suggestion made one with another type, ports or notes (§8.5).
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{
  connection?: S['NetworkConnectionItem']
  suggestion?: S['NetworkSuggestionItem']
  start?: { kind: S['NetworkDeviceKind']; id: string; name: string }
}>()
const emit = defineEmits<{ saved: [connection: S['NetworkConnectionItem']] }>()
const session = useSession()
const { run, busy } = useActions()

type Device = { value: string; label: string; kind: S['NetworkDeviceKind']; id: string }
const mode = computed(() => (props.connection ? 'edit' : props.suggestion ? 'suggestion' : 'new'))

const types = useQuery({
  queryKey: [...keys.network, 'types'],
  queryFn: () => api.network.connectionTypes(),
  staleTime: Infinity,
})
const routers = useQuery({
  queryKey: [...keys.network, 'graph', 'routers'],
  queryFn: () => api.network.graph({ routers: 'all' }),
  enabled: computed(() => visible.value && mode.value === 'new'),
})
const hosts = useQuery({
  queryKey: [...keys.hosts, 'network-picker'],
  queryFn: () => api.hosts.list({ pageSize: 500, sort: 'name' }),
  enabled: computed(() => visible.value && mode.value === 'new' && session.can('Monitoring', 'View')),
})
const devices = computed<Device[]>(() => [
  ...(routers.data.value?.nodes ?? [])
    .filter((n) => n.kind === 'Router' && n.sensorId)
    .map((n) => ({
      value: `router:${n.sensorId}`,
      label: `${n.name} (router)`,
      kind: 'Router' as const,
      id: n.sensorId!,
    })),
  ...(hosts.data.value?.items ?? []).map((h) => ({
    value: `host:${h.id}`,
    label: h.name,
    kind: 'Host' as const,
    id: h.id,
  })),
])
const typeGroups = computed(() => {
  const all = types.data.value ?? []
  const media = [...new Set(all.map((t) => t.medium))]
  return media.map((m) => ({
    label: mediumLabels[m],
    items: all.filter((t) => t.medium === m).map((t) => ({ label: t.label, value: t.code })),
  }))
})

const a = ref<string | null>(null)
const b = ref<string | null>(null)
const aPort = ref<string | null>(null)
const bPort = ref<string | null>(null)
const type = ref<string | null>(null)
const notes = ref('')

// Immediate: the dialog is often mounted already open (v-if with the row it is for).
watch(
  visible,
  (open) => {
    if (!open) return
    if (props.connection) {
      aPort.value = props.connection.a.interface ?? null
      bPort.value = props.connection.b.interface ?? null
      type.value = props.connection.type
      notes.value = props.connection.notes ?? ''
    } else if (props.suggestion) {
      aPort.value = props.suggestion.a.interface ?? null
      bPort.value = props.suggestion.b.interface ?? null
      type.value = props.suggestion.type ?? null
      notes.value = ''
    } else {
      a.value = props.start ? `${props.start.kind === 'Router' ? 'router' : 'host'}:${props.start.id}` : null
      b.value = null
      aPort.value = null
      bPort.value = null
      type.value = null
      notes.value = ''
    }
  },
  { immediate: true },
)

function parse(value: string | null): { kind: S['NetworkDeviceKind']; id: string } | null {
  if (!value) return null
  const [kind, id] = value.split(':')
  return id ? { kind: kind === 'router' ? 'Router' : 'Host', id } : null
}
function endOf(side: 'a' | 'b') {
  if (props.connection) return props.connection[side]
  if (props.suggestion) return props.suggestion[side]
  return null
}
function deviceOf(side: 'a' | 'b') {
  const end = endOf(side)
  if (end) return { kind: end.kind, id: end.kind === 'Router' && end.sensorId ? end.sensorId : end.hostId }
  return parse(side === 'a' ? a.value : b.value)
}
const aDevice = computed(() => deviceOf('a'))
const bDevice = computed(() => deviceOf('b'))
const aDetail = useQuery({
  queryKey: computed(() => [...keys.network, 'device', aDevice.value?.id ?? '']),
  queryFn: () => api.network.device(aDevice.value!.kind, aDevice.value!.id),
  enabled: computed(() => visible.value && !!aDevice.value),
})
const bDetail = useQuery({
  queryKey: computed(() => [...keys.network, 'device', bDevice.value?.id ?? '']),
  queryFn: () => api.network.device(bDevice.value!.kind, bDevice.value!.id),
  enabled: computed(() => visible.value && !!bDevice.value),
})
function portOptions(detail: S['NetworkDeviceDetail'] | undefined): { label: string; value: string }[] {
  if (!detail) return []
  if (detail.kind === 'Router') {
    return detail.ports.map((p) => ({
      label: p.detectedTypeLabel ? `${p.name} (${p.detectedTypeLabel})` : p.name,
      value: p.name,
    }))
  }
  return detail.adapters.map((x) => ({ label: `${x.adapter} (${x.mac})`, value: x.adapter }))
}
// The router port's proposed type, when the person hasn't chosen one.
watch([aPort, bPort], () => {
  if (type.value || mode.value === 'edit') return
  const proposal = [aDetail.data.value, bDetail.data.value]
    .flatMap((d, i) => d?.ports.filter((p) => p.name === (i === 0 ? aPort.value : bPort.value)) ?? [])
    .map((p) => p.detectedType)
    .find(Boolean)
  if (proposal) type.value = proposal
})

const title = computed(() =>
  mode.value === 'edit' ? 'Edit connection' : mode.value === 'suggestion' ? 'Make connection' : 'New connection',
)
const valid = computed(
  () => !!type.value && (mode.value !== 'new' || (!!aDevice.value && !!bDevice.value && a.value !== b.value)),
)

async function save() {
  const port = (v: string | null) => (v && v.trim() ? v.trim() : undefined)
  let saved: S['NetworkConnectionItem'] | undefined
  if (props.connection) {
    saved = await run(
      () =>
        api.network.updateConnection(props.connection!.id, {
          aInterface: aPort.value ?? '',
          bInterface: bPort.value ?? '',
          type: type.value,
          notes: notes.value,
        }),
      { success: 'Connection saved', failure: 'Not saved', invalidate: [keys.network] },
    )
  } else if (props.suggestion) {
    saved = await run(
      () =>
        api.network.accept(props.suggestion!.id, {
          type: type.value,
          aInterface: aPort.value ?? '',
          bInterface: bPort.value ?? '',
          notes: notes.value || undefined,
        }),
      { success: 'Made a connection', failure: 'Not made', invalidate: [keys.network] },
    )
  } else {
    const end = (d: { kind: S['NetworkDeviceKind']; id: string } | null, p: string | null) =>
      d?.kind === 'Router' ? { sensorId: d.id, interface: port(p) } : { hostId: d!.id, interface: port(p) }
    saved = await run(
      () =>
        api.network.createConnection({
          a: end(aDevice.value, aPort.value),
          b: end(bDevice.value, bPort.value),
          type: type.value!,
          notes: notes.value || undefined,
        }),
      { success: 'Connection added', failure: 'Not added', invalidate: [keys.network] },
    )
  }
  if (saved) {
    emit('saved', saved)
    visible.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="title"
    modal
    :style="{ width: '36rem' }"
    :breakpoints="{ '599px': '95vw' }"
  >
    <div class="form">
      <div v-for="side in ['a', 'b'] as const" :key="side" class="end">
        <label :for="`end-${side}`">{{ side === 'a' ? 'From' : 'To' }}</label>
        <div v-if="mode !== 'new'" class="fixed">{{ endLabel({ name: endOf(side)!.name, interface: null }) }}</div>
        <Select
          v-else
          :id="`end-${side}`"
          :model-value="side === 'a' ? a : b"
          :options="devices"
          option-label="label"
          option-value="value"
          filter
          :loading="routers.isPending.value"
          placeholder="A router or host"
          :aria-label="side === 'a' ? 'From' : 'To'"
          @update:model-value="(v: string) => (side === 'a' ? (a = v) : (b = v))"
        />
        <Select
          :model-value="side === 'a' ? aPort : bPort"
          :options="portOptions(side === 'a' ? aDetail.data.value : bDetail.data.value)"
          option-label="label"
          option-value="value"
          editable
          show-clear
          placeholder="Port or adapter (optional)"
          :aria-label="`${side === 'a' ? 'From' : 'To'} port`"
          @update:model-value="(v: string | null) => (side === 'a' ? (aPort = v) : (bPort = v))"
        />
      </div>
      <div class="field">
        <label for="connection-type">Type</label>
        <Select
          id="connection-type"
          v-model="type"
          :options="typeGroups"
          option-label="label"
          option-value="value"
          option-group-label="label"
          option-group-children="items"
          filter
          placeholder="Medium and speed"
          aria-label="Type"
        />
      </div>
      <div class="field">
        <label for="connection-notes">Notes</label>
        <Textarea id="connection-notes" v-model="notes" rows="3" auto-resize placeholder="Patch panel, circuit ID…" />
      </div>
    </div>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        :label="mode === 'edit' ? 'Save' : mode === 'suggestion' ? 'Make connection' : 'Add'"
        :loading="busy"
        :disabled="!valid"
        @click="save"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.form {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.end,
.field {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.fixed {
  font-weight: 600;
}
</style>
