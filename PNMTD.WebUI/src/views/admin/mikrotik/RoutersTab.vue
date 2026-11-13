<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import CodeTag from '@/components/CodeTag.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import { useServerTable } from '@/composables/useServerTable'
import { modeLabel } from '@/views/sensors/mikrotik/mikrotikDraft'
import UpdateDialog from '@/views/sensors/mikrotik/UpdateDialog.vue'
import { scheduleLabel } from '@/views/sensors/mikrotik/backupSchedule'
import { fixLabel, fixSeverity } from '@/views/sensors/mikrotik/security'
import CommandDialog from './CommandDialog.vue'
import RouterBackupDialog from './RouterBackupDialog.vue'

/**
 * Every router (§11.2): its state, version against the latest of its channel, and whether its script is up to
 * date. Routers chosen here, across pages and filters, get one batch: Run command… (§8.2) or Update… (§7.4),
 * or one backup schedule (Backup settings…, docs/mikrotik-backup-plan.md §2.2).
 */
const filter = ref<'' | 'update' | 'outdated' | 'failing'>('')
const filters = computed(() => ({
  updateAvailable: filter.value === 'update' ? true : undefined,
  scriptOutdated: filter.value === 'outdated' ? true : undefined,
  state: filter.value === 'failing' ? ('Failing' as S['SensorState']) : undefined,
}))
const table = useServerTable<S['MikrotikFleetItem'] & { id: string }>({
  key: [...keys.mikrotik, 'routers'],
  fetch: async (q) => {
    const page = await api.mikrotik.routers(q)
    return { ...page, items: page.items.map((i) => ({ ...i, id: i.sensorId })) }
  },
  defaultSort: 'name',
  pageSize: 50,
  filters,
  syncUrl: true,
})
type Router = S['MikrotikFleetItem'] & { id: string }
const router = useRouter()
const selected = ref(new Map<string, Router>())
const loginModes = (r: Router) => r.mode !== 'WireGuardOnly'
const onPage = computed(() => table.items.value.filter(loginModes))
const allOnPage = computed(() => onPage.value.length > 0 && onPage.value.every((r) => selected.value.has(r.id)))
function toggle(r: Router, on: boolean) {
  const next = new Map(selected.value)
  if (on) next.set(r.id, r)
  else next.delete(r.id)
  selected.value = next
}
function toggleAll(on: boolean) {
  const next = new Map(selected.value)
  for (const r of onPage.value) {
    if (on) next.set(r.id, r)
    else next.delete(r.id)
  }
  selected.value = next
}
const chosen = computed(() => [...selected.value.values()])
const commanding = ref(false)
const updating = ref(false)
const backingUp = ref(false)
function commandStarted(created: S['MikrotikCommandsCreated']) {
  void router.push({ query: { tab: 'jobs', batch: created.batchId } })
}

const choices = [
  { label: 'All routers', value: '' },
  { label: 'Behind on updates', value: 'update' },
  { label: 'Script outdated', value: 'outdated' },
  { label: 'Failing', value: 'failing' },
]
</script>

<template>
  <div class="tab">
    <div class="toolbar">
      <Select
        v-model="filter"
        :options="choices"
        option-label="label"
        option-value="value"
        size="small"
        placeholder="All routers"
        aria-label="Filter"
      />
      <span class="spacer" />
      <template v-if="chosen.length">
        <span class="muted">{{ chosen.length }} chosen</span>
        <Button label="Clear" size="small" text severity="secondary" @click="selected = new Map()" />
      </template>
      <Button
        label="Run command…"
        icon="pi pi-code"
        size="small"
        :disabled="!chosen.length"
        @click="commanding = true"
      />
      <Button
        label="Update…"
        icon="pi pi-upload"
        size="small"
        severity="secondary"
        :disabled="!chosen.length"
        @click="updating = true"
      />
      <Button
        label="Backup settings…"
        icon="pi pi-history"
        size="small"
        severity="secondary"
        :disabled="!chosen.length"
        @click="backingUp = true"
      />
    </div>
    <ListTable :table="table" what="routers" empty-text="No MikroTik router yet: add one as a sensor of a host.">
      <Column style="width: 2.5rem">
        <template #header>
          <Checkbox
            :model-value="allOnPage"
            binary
            :disabled="!onPage.length"
            aria-label="Choose every router on this page"
            @update:model-value="(v: boolean) => toggleAll(v)"
          />
        </template>
        <template #body="{ data }">
          <Checkbox
            v-if="loginModes(data)"
            :model-value="selected.has(data.id)"
            binary
            :aria-label="`Choose ${data.name}`"
            @update:model-value="(v: boolean) => toggle(data, v)"
          />
        </template>
      </Column>
      <Column header="Router">
        <template #body="{ data }">
          <span class="line">
            <StatusBadge :status="data.state" icon-only />
            <RouterLink :to="{ name: 'sensor', params: { id: data.sensorId } }">{{ data.name }}</RouterLink>
          </span>
          <div class="muted small">{{ data.host.name }} · {{ data.identity ?? '–' }} · {{ data.model ?? '' }}</div>
        </template>
      </Column>
      <Column header="Connection" style="width: 11rem">
        <template #body="{ data }">
          {{ modeLabel(data.mode) }}
          <div class="muted small mono">{{ data.address ?? '' }}</div>
        </template>
      </Column>
      <Column header="RouterOS" style="width: 12rem">
        <template #body="{ data }">
          <span class="mono">{{ data.version ?? '–' }}</span> <span class="muted small">{{ data.channel ?? '' }}</span>
          <div v-if="data.updateAvailable" class="small update">{{ data.latestVersion }} available</div>
          <Tag
            v-if="data.securityFix"
            :value="fixLabel(data.securityFix)"
            :severity="fixSeverity(data.securityFix)"
            icon="pi pi-shield"
            class="small"
          />
        </template>
      </Column>
      <Column header="State">
        <template #body="{ data }">
          <CodeTag :code="data.lastCode" compact />
          <span class="small ellipsis">{{ data.lastMessage }}</span>
          <div class="small">
            <Tag v-if="data.setupState !== 'Ready'" :value="data.setupState" severity="secondary" />
            <Tag v-if="data.scriptOutdated" value="script outdated" severity="warn" />
          </div>
        </template>
      </Column>
      <Column header="Backups" style="width: 8rem">
        <template #body="{ data }">
          <span v-if="loginModes(data)" :class="{ muted: data.backupIntervalHours === null }">{{
            scheduleLabel(data.backupIntervalHours)
          }}</span>
          <i
            v-if="data.encryptBackups"
            v-tooltip.top="'Encrypted with the backup key'"
            class="pi pi-lock lock"
            aria-label="encrypted"
          />
        </template>
      </Column>
      <Column header="Last poll" style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.lastSuccessAt" /></template>
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <StatusBadge :status="item.state" icon-only />
          <RouterLink :to="{ name: 'sensor', params: { id: item.sensorId } }">{{ item.name }}</RouterLink>
          <span class="mono muted">{{ item.version }}</span>
        </div>
      </template>
    </ListTable>
    <CommandDialog
      v-model:visible="commanding"
      :routers="chosen.map((r) => ({ id: r.sensorId, name: r.name }))"
      @started="commandStarted"
    />
    <RouterBackupDialog v-model:visible="backingUp" :routers="chosen.map((r) => ({ id: r.sensorId, name: r.name }))" />
    <UpdateDialog
      v-model:visible="updating"
      :routers="
        chosen.map((r) => ({
          id: r.sensorId,
          name: r.name,
          identity: r.identity,
          from: r.version,
          to: r.latestVersion,
          method: r.updateMethod,
          checkStorage: r.updateCheckStorage,
          lowStorage: r.lowStorage,
          freeStorage: r.freeStorage,
        }))
      "
    />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.small {
  font-size: 0.857rem;
}

.mono {
  font-family: var(--pnmt-mono);
}

.update {
  color: var(--pnmt-warn);
}

.toolbar {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.spacer {
  flex: 1;
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.lock {
  margin-left: 0.35rem;
  font-size: 0.8rem;
  color: var(--pnmt-muted);
}
</style>
