<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'

/**
 * WMI (docs/windows-sensor-plan.md §16): every query the agent runs, per section, with its last run, rows, time and status; PNMT
 * switches built-in queries off or gives them another timeout, and adds read-only queries of its own whose rows come back here.
 * Saved settings go to the agent with its configuration; what a local administrator switched off in the agent's window wins.
 * Admin.
 */
const props = defineProps<{ server: S['WindowsServerDetail'] }>()
const id = computed(() => props.server.server.sensorId)
const wmi = useQuery({
  queryKey: computed(() => [...keys.windowsServer(id.value), 'wmi']),
  queryFn: () => api.windows.wmi(id.value),
  refetchInterval: 30_000,
})
const view = computed(() => wmi.data.value ?? null)

// The settings being edited: switches and timeouts by query id, and the own queries.
type Setting = { enabled: boolean; timeoutSeconds: number | null }
const settings = ref<Record<string, Setting>>({})
const custom = ref<S['CustomWmiQuery'][]>([])
const saved = ref('')
const snapshot = () => JSON.stringify({ s: settings.value, c: custom.value })
function load(v: S['WindowsWmiView']) {
  settings.value = Object.fromEntries(
    v.settings.overrides.map((o) => [
      o.id.toLowerCase(),
      { enabled: o.enabled, timeoutSeconds: o.timeoutSeconds ?? null },
    ]),
  )
  custom.value = v.settings.custom.map((c) => ({ ...c }))
  saved.value = snapshot()
}
const dirty = computed(() => saved.value !== '' && snapshot() !== saved.value)
watch(view, (v) => v && !dirty.value && load(v), { immediate: true })

const setting = (queryId: string): Setting =>
  settings.value[queryId.toLowerCase()] ?? { enabled: true, timeoutSeconds: null }
function setEnabled(queryId: string, enabled: boolean) {
  settings.value = { ...settings.value, [queryId.toLowerCase()]: { ...setting(queryId), enabled } }
}
function setTimeout(queryId: string, timeoutSeconds: number | null) {
  settings.value = { ...settings.value, [queryId.toLowerCase()]: { ...setting(queryId), timeoutSeconds } }
}

// The built-in queries as reported, and those set here but not reported yet.
const queries = computed(() => {
  const reported = view.value?.section?.queries ?? []
  const known = new Set(reported.map((q) => q.id.toLowerCase()))
  const unreported = Object.keys(settings.value)
    .filter((k) => !known.has(k))
    .map((k): S['WmiQueryState'] => ({
      id: k,
      namespace: k.split(':')[0] ?? '',
      class: k.split(':')[1] ?? k,
      wql: '',
      section: null,
      enabled: setting(k).enabled,
      offBy: null,
      timeoutSeconds: setting(k).timeoutSeconds ?? 15,
      lastRunAt: null,
      rows: null,
      ms: null,
      status: 'not run yet',
      error: null,
    }))
  return [...reported, ...unreported]
})
const results = computed(() => new Map((view.value?.section?.custom ?? []).map((r) => [r.id, r])))
const statusSeverity = (status: string) =>
  status === 'ok'
    ? 'success'
    : status === 'off' || status === 'not run yet'
      ? 'secondary'
      : status === 'not available'
        ? 'warn'
        : 'danger'

const { run, busy } = useActions()
async function save() {
  const overrides = Object.entries(settings.value)
    .filter(([, s]) => !s.enabled || s.timeoutSeconds !== null)
    .map(([queryId, s]) => ({ id: queryId, enabled: s.enabled, timeoutSeconds: s.timeoutSeconds }))
  const v = await run(() => api.windows.saveWmi(id.value, { overrides, custom: custom.value }), {
    success: 'Saved: the agent gets it with its configuration',
    invalidate: [keys.windowsServer(id.value)],
  })
  if (v) load(v)
}
const collect = () =>
  run(
    async () => {
      const r = await api.windows.collect(id.value, ['wmi'])
      if (!r.ok) throw new Error(r.error ?? 'The agent refused.')
      return r
    },
    { success: 'Collected', invalidate: [keys.windowsServer(id.value)] },
  )

// An own query, added or changed in a dialog; saved with the rest.
const empty = '00000000-0000-0000-0000-000000000000'
const editing = ref<S['CustomWmiQuery'] | null>(null)
const editingIndex = ref(-1)
function edit(index: number) {
  editingIndex.value = index
  editing.value =
    index < 0
      ? {
          id: empty,
          name: '',
          namespace: 'root\\cimv2',
          wql: 'SELECT Name, State FROM Win32_Service WHERE StartMode = "Auto"',
          intervalMinutes: 15,
          timeoutSeconds: 30,
          enabled: true,
        }
      : { ...custom.value[index]! }
}
function apply() {
  if (!editing.value) return
  const next = [...custom.value]
  if (editingIndex.value < 0) next.push(editing.value)
  else next[editingIndex.value] = editing.value
  custom.value = next
  editing.value = null
}
const remove = (index: number) => (custom.value = custom.value.filter((_, i) => i !== index))

// An own query's rows, in a wide window.
const shown = ref<S['CustomWmiResult'] | null>(null)
const shownRows = computed(() =>
  (shown.value?.rows ?? []).map((row) => Object.fromEntries(row.map((value, i) => [`c${i}`, value ?? '']))),
)
</script>

<template>
  <div class="tab">
    <div class="bar">
      <span v-if="view?.collectedAt" class="muted small">Reported <RelativeTime :value="view.collectedAt" /></span>
      <span v-else-if="wmi.isPending.value" class="muted small">Loading…</span>
      <span v-else class="muted small"
        >Not reported yet: an agent from before WMI was adjustable, or one that hasn't reported since.</span
      >
      <span class="spacer" />
      <Button
        label="Collect now"
        icon="pi pi-refresh"
        size="small"
        severity="secondary"
        :disabled="!view?.connected"
        :loading="busy"
        @click="collect"
      />
      <Button v-if="dirty" label="Discard" size="small" severity="secondary" text @click="view && load(view)" />
      <Button label="Save" icon="pi pi-check" size="small" :disabled="!dirty" :loading="busy" @click="save" />
    </div>
    <Message v-if="dirty" severity="info" :closable="false" size="small">
      Not saved yet. Saved, the agent gets the settings with its configuration and runs the sections concerned again.
    </Message>

    <section class="panel">
      <div class="panel-header">The agent's queries</div>
      <div class="panel-body">
        <p class="muted small intro">
          What the agent asks WMI for its sections. Switched off, a query isn't run and its part of the section stays
          empty; a timeout here replaces the agent's (15 s for most). A local administrator can switch any query off in
          the agent's window on the server, which wins.
        </p>
        <DataTable :value="queries" size="small" data-key="id" sort-field="section" :sort-order="1">
          <template #empty><span class="muted">None reported yet.</span></template>
          <Column header="Query" sortable sort-field="class">
            <template #body="{ data }">
              <strong :title="data.wql">{{ data.class }}</strong>
              <div class="muted small mono">{{ data.namespace }}</div>
            </template>
          </Column>
          <Column header="Section" field="section" sortable style="width: 9rem">
            <template #body="{ data }">{{ data.section ?? '–' }}</template>
          </Column>
          <Column header="Status" style="width: 14rem">
            <template #body="{ data }">
              <div class="tag-line">
                <Tag :value="data.status" :severity="statusSeverity(data.status)" />
                <Tag v-if="data.offBy === 'server'" value="off on the server" severity="warn" />
              </div>
              <div v-if="data.error" class="small muted error-text">{{ data.error }}</div>
            </template>
          </Column>
          <Column header="Rows" style="width: 5rem">
            <template #body="{ data }">{{ data.rows ?? '–' }}</template>
          </Column>
          <Column header="Time" style="width: 6rem">
            <template #body="{ data }">{{ data.ms !== null ? `${Math.round(data.ms)} ms` : '–' }}</template>
          </Column>
          <Column header="Last run" style="width: 8rem">
            <template #body="{ data }"
              ><RelativeTime v-if="data.lastRunAt" :value="data.lastRunAt" /><span v-else class="muted"
                >–</span
              ></template
            >
          </Column>
          <Column header="On" style="width: 5rem">
            <template #body="{ data }">
              <ToggleSwitch
                :model-value="setting(data.id).enabled"
                :aria-label="`${data.class} on`"
                @update:model-value="(v: boolean) => setEnabled(data.id, v)"
              />
            </template>
          </Column>
          <Column header="Timeout (s)" style="width: 8rem">
            <template #body="{ data }">
              <InputNumber
                :model-value="setting(data.id).timeoutSeconds"
                :placeholder="String(data.timeoutSeconds)"
                :min="1"
                :max="600"
                size="small"
                input-class="timeout-input"
                :aria-label="`${data.class} timeout`"
                @update:model-value="(v: number | null) => setTimeout(data.id, v)"
              />
            </template>
          </Column>
        </DataTable>
      </div>
    </section>

    <section class="panel">
      <div class="panel-header header-row">
        Own queries
        <span class="spacer" />
        <Button label="Add query…" icon="pi pi-plus" size="small" severity="secondary" @click="edit(-1)" />
      </div>
      <div class="panel-body">
        <p class="muted small intro">
          Read-only SELECT queries the agent runs on their own interval; their rows (at most 200) come back here. Saved
          with the settings above.
        </p>
        <DataTable :value="custom" size="small" data-key="id">
          <template #empty><span class="muted">None.</span></template>
          <Column header="Name">
            <template #body="{ data }">
              <strong>{{ data.name }}</strong>
              <div class="muted small mono ellipsis" :title="data.wql">{{ data.namespace }} · {{ data.wql }}</div>
            </template>
          </Column>
          <Column header="Every" style="width: 7rem">
            <template #body="{ data }">{{ data.intervalMinutes }} min</template>
          </Column>
          <Column header="Result" style="width: 16rem">
            <template #body="{ data }">
              <template v-if="results.get(data.id)">
                <div class="tag-line">
                  <Tag :value="results.get(data.id)!.status" :severity="statusSeverity(results.get(data.id)!.status)" />
                  <span v-if="results.get(data.id)!.status === 'ok'" class="small"
                    >{{ results.get(data.id)!.rows.length }}{{ results.get(data.id)!.truncated ? '+' : '' }} rows</span
                  >
                  <RelativeTime
                    v-if="results.get(data.id)!.lastRunAt"
                    :value="results.get(data.id)!.lastRunAt!"
                    class="small muted"
                  />
                </div>
                <div v-if="results.get(data.id)!.error" class="small muted error-text">
                  {{ results.get(data.id)!.error }}
                </div>
              </template>
              <span v-else class="muted small">{{ data.id === empty ? 'not saved yet' : 'not reported yet' }}</span>
            </template>
          </Column>
          <Column header="On" style="width: 5rem">
            <template #body="{ data, index }">
              <ToggleSwitch
                :model-value="data.enabled"
                :aria-label="`${data.name} on`"
                @update:model-value="(v: boolean) => (custom[index] = { ...data, enabled: v })"
              />
            </template>
          </Column>
          <Column style="width: 15rem">
            <template #body="{ data, index }">
              <div class="actions">
                <Button
                  label="Rows"
                  size="small"
                  severity="secondary"
                  :disabled="!results.get(data.id)?.columns.length"
                  @click="shown = results.get(data.id) ?? null"
                />
                <Button label="Edit…" size="small" severity="secondary" text @click="edit(index)" />
                <Button label="Remove" size="small" severity="danger" text @click="remove(index)" />
              </div>
            </template>
          </Column>
        </DataTable>
      </div>
    </section>

    <Dialog
      :visible="editing !== null"
      :header="editingIndex < 0 ? 'Add a WMI query' : 'WMI query'"
      modal
      :style="{ width: '44rem' }"
      :breakpoints="{ '700px': '96vw' }"
      @update:visible="(v: boolean) => !v && (editing = null)"
    >
      <form v-if="editing" class="form" @submit.prevent="apply">
        <div class="field">
          <label for="wmi-name">Name</label>
          <InputText id="wmi-name" v-model="editing.name" maxlength="100" />
        </div>
        <div class="field">
          <label for="wmi-namespace">Namespace</label>
          <InputText id="wmi-namespace" v-model="editing.namespace" class="mono" />
          <small class="field-hint">root\cimv2, root\StandardCimv2, root\Microsoft\Windows\Storage, …</small>
        </div>
        <div class="field">
          <label for="wmi-wql">Query</label>
          <Textarea id="wmi-wql" v-model="editing.wql" rows="4" class="mono" auto-resize />
          <small class="field-hint">One SELECT … FROM class [WHERE …]: WMI is only read.</small>
        </div>
        <div class="form-grid">
          <div class="field">
            <label for="wmi-interval">Every (minutes)</label>
            <InputNumber v-model="editing.intervalMinutes" input-id="wmi-interval" :min="1" :max="1440" />
          </div>
          <div class="field">
            <label for="wmi-timeout">Timeout (seconds)</label>
            <InputNumber v-model="editing.timeoutSeconds" input-id="wmi-timeout" :min="1" :max="600" />
          </div>
        </div>
      </form>
      <template #footer>
        <Button label="Cancel" text severity="secondary" @click="editing = null" />
        <Button
          label="Apply"
          icon="pi pi-check"
          :disabled="!editing?.name.trim() || !editing?.wql.trim()"
          @click="apply"
        />
      </template>
    </Dialog>

    <Dialog
      :visible="shown !== null"
      :header="shown ? `${shown.name}: ${shown.rows.length}${shown.truncated ? '+' : ''} rows` : ''"
      modal
      maximizable
      dismissable-mask
      :style="{ width: '90vw', maxWidth: '90rem' }"
      :breakpoints="{ '700px': '100vw' }"
      @update:visible="(v: boolean) => !v && (shown = null)"
    >
      <template v-if="shown">
        <p class="muted small mono">{{ shown.namespace }} · {{ shown.wql }}</p>
        <DataTable :value="shownRows" size="small" scrollable scroll-height="60vh">
          <Column v-for="(c, i) in shown.columns" :key="c" :field="`c${i}`" :header="c" sortable>
            <template #body="{ data }"
              ><span class="small">{{ data[`c${i}`] }}</span></template
            >
          </Column>
        </DataTable>
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.bar,
.header-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.spacer {
  flex: 1;
}

.intro {
  margin: 0 0 var(--pnmt-space-2);
}

.tag-line,
.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
}

.error-text {
  margin-top: var(--pnmt-space-1);
  word-break: break-word;
}

:deep(.timeout-input) {
  width: 5rem;
}

.small {
  font-size: 0.857rem;
}
</style>
