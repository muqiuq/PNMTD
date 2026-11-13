<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import { ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'

/**
 * A diagnostics set (docs/windows-sensor-plan.md §5.1, §10.3): a name, a description, and up to 50 typed checks (ping: target,
 * count, timeout, limits; DNS: name, type, server, expectation). PNMTD validates them: at most 10 pings of at most 5 s.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ set?: S['DiagnosticSetDetail'] | null }>()
const emit = defineEmits<{ saved: [set: S['DiagnosticSetDetail']] }>()

interface Row {
  name: string
  type: S['DiagnosticCheckType']
  ping: S['PingParameters']
  dns: S['DnsParameters']
}
const newPing = (): S['PingParameters'] => ({
  target: '',
  count: 4,
  timeoutMs: 1000,
  family: 'Any',
  maxLossPercent: 0,
  maxAvgMs: null,
})
const newDns = (): S['DnsParameters'] => ({ name: '', recordType: 'A', server: null, expect: null, maxMs: null })
const name = ref('')
const description = ref('')
const rows = ref<Row[]>([])
const errors = ref<Record<string, string>>({})
watch(
  visible,
  (open) => {
    if (!open) return
    errors.value = {}
    name.value = props.set?.name ?? ''
    description.value = props.set?.description ?? ''
    rows.value = (props.set?.checks ?? []).map((c) => ({
      name: c.name,
      type: c.type,
      ping: c.ping ?? newPing(),
      dns: c.dns ?? newDns(),
    }))
    if (!rows.value.length) add('Ping')
  },
  { immediate: true },
)
function add(type: S['DiagnosticCheckType']) {
  rows.value.push({ name: '', type, ping: newPing(), dns: newDns() })
}
function move(i: number, by: number) {
  const [row] = rows.value.splice(i, 1)
  rows.value.splice(i + by, 0, row)
}
const recordTypes: S['DnsRecordKind'][] = ['A', 'AAAA', 'CNAME', 'MX', 'TXT', 'SRV', 'PTR', 'NS', 'SOA']

const { run, busy } = useActions()
async function save() {
  const body: S['DiagnosticSetRequest'] = {
    name: name.value,
    description: description.value || null,
    checks: rows.value.map((r) => ({
      name: r.name,
      type: r.type,
      ping: r.type === 'Ping' ? r.ping : null,
      dns: r.type === 'Dns' ? { ...r.dns, server: r.dns.server || null, expect: r.dns.expect || null } : null,
    })),
  }
  const saved = await run(
    () => (props.set ? api.diagnostics.updateSet(props.set.id, body) : api.diagnostics.createSet(body)),
    {
      success: 'Saved',
      invalidate: [keys.diagnostics],
    },
  )
  if (saved) {
    emit('saved', saved)
    visible.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="set ? `Edit ${set.name}` : 'New diagnostics set'"
    modal
    :style="{ width: '60rem' }"
    :breakpoints="{ '960px': '95vw' }"
  >
    <form class="form" @submit.prevent="save">
      <div class="form-grid">
        <div class="field">
          <label for="set-name">Name</label>
          <InputText id="set-name" v-model="name" maxlength="200" placeholder="branch connectivity" />
        </div>
        <div class="field">
          <label for="set-description">Description</label>
          <InputText id="set-description" v-model="description" maxlength="2000" />
        </div>
      </div>
      <div v-for="(row, i) in rows" :key="i" class="check panel">
        <div class="check-head">
          <Select v-model="row.type" :options="['Ping', 'Dns']" size="small" aria-label="Type" />
          <InputText v-model="row.name" placeholder="Name" size="small" maxlength="200" />
          <span class="spacer" />
          <Button icon="pi pi-arrow-up" text size="small" :disabled="i === 0" aria-label="Up" @click="move(i, -1)" />
          <Button
            icon="pi pi-arrow-down"
            text
            size="small"
            :disabled="i === rows.length - 1"
            aria-label="Down"
            @click="move(i, 1)"
          />
          <Button
            icon="pi pi-trash"
            text
            size="small"
            severity="danger"
            aria-label="Remove"
            @click="rows.splice(i, 1)"
          />
        </div>
        <div v-if="row.type === 'Ping'" class="params">
          <label
            >Target <InputText v-model="row.ping.target" size="small" placeholder="gw.example or 10.0.0.1"
          /></label>
          <label
            >Count <InputNumber v-model="row.ping.count" :min="1" :max="10" size="small" input-class="num"
          /></label>
          <label
            >Timeout ms <InputNumber v-model="row.ping.timeoutMs" :min="100" :max="5000" size="small" input-class="num"
          /></label>
          <label>Family <Select v-model="row.ping.family" :options="['Any', 'IPv4', 'IPv6']" size="small" /></label>
          <label
            >Max loss %
            <InputNumber v-model="row.ping.maxLossPercent" :min="0" :max="100" size="small" input-class="num"
          /></label>
          <label
            >Max avg ms <InputNumber v-model="row.ping.maxAvgMs" :min="1" :max="5000" size="small" input-class="num"
          /></label>
        </div>
        <div v-else class="params">
          <label>Name <InputText v-model="row.dns.name" size="small" placeholder="intranet.example" /></label>
          <label>Type <Select v-model="row.dns.recordType" :options="recordTypes" size="small" /></label>
          <label>Server <InputText v-model="row.dns.server" size="small" placeholder="system's resolvers" /></label>
          <label
            >Expect <InputText v-model="row.dns.expect" size="small" placeholder="an answer, or part of one"
          /></label>
          <label
            >Max ms <InputNumber v-model="row.dns.maxMs" :min="1" :max="10000" size="small" input-class="num"
          /></label>
        </div>
      </div>
      <div class="form-actions">
        <Button label="Add ping" icon="pi pi-plus" size="small" severity="secondary" @click="add('Ping')" />
        <Button label="Add DNS" icon="pi pi-plus" size="small" severity="secondary" @click="add('Dns')" />
        <span class="muted small">{{ rows.length }} of 50 checks</span>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" text severity="secondary" @click="visible = false" />
      <Button label="Save" :loading="busy" @click="save" />
    </template>
  </Dialog>
</template>

<style scoped>
.check {
  padding: var(--pnmt-space-2);
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.check-head,
.params {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: center;
  flex-wrap: wrap;
}

.params label {
  display: flex;
  gap: var(--pnmt-space-1);
  align-items: center;
  font-size: 0.857rem;
}

.params :deep(.num) {
  width: 5rem;
}

.spacer {
  flex: 1;
}

.small {
  font-size: 0.857rem;
}
</style>
