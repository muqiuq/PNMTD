<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import DatePicker from 'primevue/datepicker'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import TunnelConfigDialog from '@/components/TunnelConfigDialog.vue'
import { useActions } from '@/composables/useActions'
import { formatBytes } from '@/format/bytes'

/**
 * Custom tunnels (docs/mikrotik-plan.md §3.5): WireGuard peers for people's devices, optionally owned by a PNMT
 * user who can download them under Account → Tunnels. Each reaches the management ports of the routers it
 * may reach, and only those with management access on.
 */
const queryClient = useQueryClient()
const { run, done, ask } = useActions()
const tunnels = useQuery({
  queryKey: [...keys.mikrotik, 'tunnels'],
  queryFn: api.wireGuard.tunnels,
  refetchInterval: 15_000,
})
const users = useQuery({ queryKey: [...keys.users, 'all'], queryFn: () => api.users.list({ pageSize: 500 }) })
const routers = useQuery({
  queryKey: [...keys.mikrotik, 'routers', 'all'],
  queryFn: () => api.mikrotik.routers({ pageSize: 500 }),
})
const routerOptions = computed(() =>
  (routers.data.value?.items ?? [])
    .filter((r) => r.mode !== 'DirectSsh')
    .map((r) => ({ label: r.name, value: r.sensorId })),
)
const userOptions = computed(() => [
  { label: 'Nobody (Admins download it)', value: null as string | null },
  ...(users.data.value?.items ?? []).map((u) => ({ label: u.username, value: u.id as string | null })),
])

const editing = ref<S['WireGuardTunnelItem'] | null>(null)
const open = ref(false)
const form = ref({
  name: '',
  notes: '',
  owner: null as string | null,
  all: true,
  routers: [] as string[],
  expires: null as Date | null,
  enabled: true,
})
const errors = ref<Record<string, string | undefined>>({})
const busy = ref(false)

function edit(t: S['WireGuardTunnelItem'] | null) {
  editing.value = t
  errors.value = {}
  form.value = {
    name: t?.name ?? '',
    notes: t?.notes ?? '',
    owner: t?.owner?.id ?? null,
    all: t?.accessAllRouters ?? true,
    routers: t?.routers.map((r) => r.id) ?? [],
    expires: t?.expiresAt ? new Date(t.expiresAt) : null,
    enabled: t?.enabled ?? true,
  }
  open.value = true
}

async function save() {
  busy.value = true
  errors.value = {}
  const body: S['WireGuardTunnelRequest'] = {
    name: form.value.name.trim(),
    notes: form.value.notes.trim() ? form.value.notes : null,
    ownerUserId: form.value.owner,
    accessAllRouters: form.value.all,
    routerIds: form.value.all ? [] : form.value.routers,
    expiresAt: form.value.expires ? form.value.expires.toISOString() : null,
    enabled: form.value.enabled,
  }
  try {
    const saved = editing.value
      ? await api.wireGuard.updateTunnel(editing.value.id, body)
      : await api.wireGuard.createTunnel(body)
    open.value = false
    await queryClient.invalidateQueries({ queryKey: [...keys.mikrotik] })
    if (!editing.value) await download(saved)
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = Object.fromEntries(
        ['name', 'notes', 'ownerUserId', 'routerIds', 'expiresAt'].map((f) => [f, e.field(f)]),
      )
    } else {
      throw e
    }
  } finally {
    busy.value = false
  }
}

const config = ref<S['WireGuardTunnelConfig'] | null>(null)
const configName = ref('')
const configOpen = ref(false)
async function download(t: S['WireGuardTunnelItem']) {
  const c = await run(() => api.wireGuard.tunnelConfig(t.id), { failure: 'No configuration' })
  if (c) {
    config.value = c
    configName.value = t.name
    configOpen.value = true
  }
}

async function regenerate(t: S['WireGuardTunnelItem']) {
  if (await ask(`New keys for ${t.name}?`, 'Every downloaded file of this tunnel stops working.', 'Regenerate')) {
    await run(() => api.wireGuard.regenerateTunnel(t.id), {
      success: 'New keys: download the file again',
      invalidate: [keys.mikrotik],
    })
  }
}

async function remove(t: S['WireGuardTunnelItem']) {
  if (await ask(`Delete ${t.name}?`, 'The tunnel stops at once, and its file is useless.')) {
    await done(() => api.wireGuard.deleteTunnel(t.id), { success: `${t.name} deleted`, invalidate: [keys.mikrotik] })
  }
}
</script>

<template>
  <div class="tab">
    <div class="toolbar">
      <span class="muted"
        >A tunnel puts a technician's laptop or phone into the network: WinBox, SSH and WebFig reach the routers with
        management access on, nothing else.</span
      >
      <Button label="New tunnel" icon="pi pi-plus" size="small" class="new" @click="edit(null)" />
    </div>
    <DataTable :value="tunnels.data.value ?? []" :loading="tunnels.isLoading.value" size="small" data-key="id">
      <template #empty><span class="muted">No custom tunnel yet.</span></template>
      <Column header="Tunnel">
        <template #body="{ data }">
          <strong>{{ data.name }}</strong>
          <Tag v-if="!data.enabled" value="disabled" severity="secondary" class="tag" />
          <Tag v-else-if="data.expired" value="expired" severity="warn" class="tag" />
          <div class="muted small">
            <span class="mono">{{ data.address }}</span><template v-if="data.owner">
              · for {{ data.owner.name }}</template
            >
          </div>
        </template>
      </Column>
      <Column header="Reaches">
        <template #body="{ data }">
          <span v-if="!data.reaches.length" class="muted">no router (management access off?)</span>
          <span v-for="r in data.reaches" :key="r.sensorId" class="chip"
            >{{ r.name }} <span class="mono">{{ r.address }}</span></span
          >
          <div class="muted small">{{ data.accessAllRouters ? 'all routers' : 'selected routers' }}</div>
        </template>
      </Column>
      <Column header="Tunnel state" style="width: 13rem">
        <template #body="{ data }">
          <span :class="data.up ? 'ok' : 'muted'">{{ data.up ? 'up' : 'down' }}</span>
          <template v-if="data.latestHandshake"> · <RelativeTime :value="data.latestHandshake" /></template>
          <div class="muted small">
            <span class="mono">{{ data.endpoint ?? '' }}</span> {{ formatBytes(data.rxBytes) }} ↓
            {{ formatBytes(data.txBytes) }} ↑
          </div>
        </template>
      </Column>
      <Column header="Expires" style="width: 8rem">
        <template #body="{ data }">
          <RelativeTime v-if="data.expiresAt" :value="data.expiresAt" />
          <span v-else class="muted">never</span>
        </template>
      </Column>
      <Column header="" style="width: 10rem">
        <template #body="{ data }">
          <Button
            v-tooltip.top="'Configuration and QR code'"
            icon="pi pi-download"
            text
            rounded
            size="small"
            :aria-label="`Download ${data.name}`"
            @click="download(data)"
          />
          <Button
            v-tooltip.top="'Edit'"
            icon="pi pi-pencil"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`Edit ${data.name}`"
            @click="edit(data)"
          />
          <Button
            v-tooltip.top="'New keys'"
            icon="pi pi-refresh"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`New keys for ${data.name}`"
            @click="regenerate(data)"
          />
          <Button
            v-tooltip.top="'Delete'"
            icon="pi pi-trash"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`Delete ${data.name}`"
            @click="remove(data)"
          />
        </template>
      </Column>
    </DataTable>

    <Dialog
      v-model:visible="open"
      :header="editing ? `Tunnel ${editing.name}` : 'New tunnel'"
      modal
      :style="{ width: '36rem' }"
      :breakpoints="{ '640px': '95vw' }"
    >
      <form id="tunnel-form" class="form" novalidate @submit.prevent="save">
        <div class="field">
          <label for="tunnel-name">Name</label>
          <InputText id="tunnel-name" v-model="form.name" placeholder="alice-laptop" :invalid="!!errors.name" />
          <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
          <small v-else class="field-hint">The WireGuard apps use the file's name: short, without spaces.</small>
        </div>
        <div class="field">
          <label for="tunnel-owner">For</label>
          <Select
            v-model="form.owner"
            input-id="tunnel-owner"
            :options="userOptions"
            option-label="label"
            option-value="value"
            filter
          />
          <small class="field-hint"
            >The user can download it under Account → Tunnels while it is enabled and not expired.</small
          >
        </div>
        <div class="switch-row">
          <ToggleSwitch v-model="form.all" input-id="tunnel-all" />
          <label for="tunnel-all">All routers with management access</label>
        </div>
        <div v-if="!form.all" class="field">
          <label for="tunnel-routers">Routers</label>
          <MultiSelect
            v-model="form.routers"
            input-id="tunnel-routers"
            :options="routerOptions"
            option-label="label"
            option-value="value"
            filter
            display="chip"
            :invalid="!!errors.routerIds"
          />
          <small v-if="errors.routerIds" class="field-error">{{ errors.routerIds }}</small>
          <small v-else class="field-hint"
            >Each only while its management access is on (an Admin's setting on the router).</small
          >
        </div>
        <div class="field">
          <label for="tunnel-expires">Expires <span class="muted">(optional)</span></label>
          <DatePicker
            v-model="form.expires"
            input-id="tunnel-expires"
            show-time
            hour-format="24"
            show-button-bar
            :invalid="!!errors.expiresAt"
          />
          <small v-if="errors.expiresAt" class="field-error">{{ errors.expiresAt }}</small>
          <small v-else class="field-hint">After it, the gateway drops the tunnel.</small>
        </div>
        <div class="field">
          <label for="tunnel-notes">Notes <span class="muted">(optional)</span></label>
          <Textarea id="tunnel-notes" v-model="form.notes" rows="2" auto-resize />
        </div>
        <div v-if="editing" class="switch-row">
          <ToggleSwitch v-model="form.enabled" input-id="tunnel-enabled" />
          <label for="tunnel-enabled">Enabled</label>
        </div>
        <Message v-if="!editing" severity="info" :closable="false" size="small"
          >Its configuration opens right after saving.</Message
        >
      </form>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="open = false" />
        <Button type="submit" form="tunnel-form" :label="editing ? 'Save' : 'Create tunnel'" :loading="busy" />
      </template>
    </Dialog>
    <TunnelConfigDialog v-model:visible="configOpen" :config="config" :name="configName" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.toolbar {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
}

.new {
  margin-left: auto;
}

.form {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.mono {
  font-family: var(--pnmt-mono);
}

.small {
  font-size: 0.857rem;
}

.ok {
  color: var(--pnmt-ok);
  font-weight: 600;
}

.tag {
  margin-left: var(--pnmt-space-2);
}

.chip {
  display: inline-block;
  margin: 0 var(--pnmt-space-2) var(--pnmt-space-1) 0;
  padding: 0 var(--pnmt-space-2);
  border-radius: var(--p-border-radius-sm);
  background: var(--pnmt-none-soft);
}
</style>
