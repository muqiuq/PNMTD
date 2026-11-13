<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute } from '@/format'
import { formatBytes } from '@/format/bytes'
import { clearBackupKey, useBackupKey } from '@/composables/useBackupKey'
import DiffView from './DiffView.vue'
import LoadKeyDialog from './LoadKeyDialog.vue'
import { backupDiff, decryptedFile, exportText, needsKey, saveFile } from './backupReading'
import { scheduleLabel } from './backupSchedule'

/**
 * A router's configuration backups (docs/mikrotik-plan.md §6, §11.1): every stored version, newest first,
 * with what changed against the one before. A version opens as text or as the diff with the version
 * before; two selected versions compare with each other. Exports with secrets are an Admin's. Encrypted versions
 * (docs/mikrotik-backup-plan.md §3.6) open with the backup key loaded in this tab, and are compared here.
 */
const props = defineProps<{ sensor: S['SensorDetail']; canWrite: boolean; isAdmin: boolean }>()
const queryClient = useQueryClient()
const { run, done, ask, busy, notify } = useActions()

const m = computed(() => props.sensor.mikrotik!)
const filters = computed(() => ({ sensorId: props.sensor.id }))
const table = useServerTable<S['MikrotikBackupItem']>({
  key: [...keys.mikrotik, 'backups', props.sensor.id],
  fetch: ({ sensorId: _sensorId, ...q }) => api.mikrotik.backups(props.sensor.id, q),
  pageSize: 25,
  filters,
})

const selected = ref<string[]>([])
function toggle(id: string, on: boolean) {
  selected.value = on
    ? [...selected.value.filter((s) => s !== id), id].slice(-2)
    : selected.value.filter((s) => s !== id)
}

type Shown = { kind: 'content'; content: S['MikrotikBackupContent'] } | { kind: 'diff'; diff: S['MikrotikBackupDiff'] }
const shown = ref<Shown | null>(null)
const readable = (b: S['MikrotikBackupItem']) =>
  props.canWrite && (props.isAdmin || (!b.includesSecrets && b.kind === 'Export'))

// The backup key, for encrypted versions: asked for when one is opened, then what was asked for goes on.
const backupKey = useBackupKey()
const loadingKey = ref(false)
const neededKey = ref<string | null>(null)
let pending: (() => Promise<void>) | null = null
const anyEncrypted = computed(() => m.value.encryptBackups || table.items.value.some((i) => !!i.encryptedWith))
async function withKey(items: (S['MikrotikBackupItem'] | undefined)[], action: () => Promise<void>) {
  if (needsKey(...items) && !backupKey.loaded.value) {
    neededKey.value = items.find((i) => i?.encryptedWith)?.encryptedWith ?? null
    pending = action
    loadingKey.value = true
    return
  }
  await action()
}
function openKeyDialog() {
  neededKey.value = null
  pending = null
  loadingKey.value = true
}
async function keyLoaded() {
  const action = pending
  pending = null
  if (action) await action()
}
const exportBefore = (b: S['MikrotikBackupItem']) =>
  table.items.value.filter((i) => i.kind === 'Export' && i.number < b.number).sort((x, y) => y.number - x.number)[0]

async function view(b: S['MikrotikBackupItem']) {
  await withKey([b], async () => {
    const read = await run(() => exportText(props.sensor.id, b.id), { failure: 'Not shown' })
    if (read) shown.value = { kind: 'content', content: { backup: read.item, content: read.text } }
  })
}

async function changes(b: S['MikrotikBackupItem'], against?: S['MikrotikBackupItem']) {
  await withKey([b, against ?? exportBefore(b)], async () => {
    const diff = await run(() => backupDiff(props.sensor.id, b.id, against?.id), { failure: 'Not compared' })
    if (diff) shown.value = { kind: 'diff', diff }
  })
}

async function compareSelected() {
  const items = table.items.value.filter((i) => selected.value.includes(i.id)).sort((a, b) => a.number - b.number)
  if (items.length === 2) await changes(items[1]!, items[0]!)
}

/** An encrypted version, decrypted here: the .rsc or .backup as the router wrote it. */
async function downloadDecrypted(b: S['MikrotikBackupItem']) {
  const blob = await run(() => decryptedFile(props.sensor.id, b), { failure: 'Not decrypted' })
  if (blob) saveFile(blob, `${m.value.identity ?? 'router'}-v${b.number}.${b.kind === 'Binary' ? 'backup' : 'rsc'}`)
}

async function backUp() {
  const result = await run(() => api.mikrotik.backUp(props.sensor.id), { failure: 'No backup' })
  if (result) {
    notify.success(result.changed ? 'Backed up' : 'Unchanged', result.message)
    await queryClient.invalidateQueries({ queryKey: [...keys.mikrotik, 'backups', props.sensor.id] })
    await queryClient.invalidateQueries({ queryKey: [...keys.sensor(props.sensor.id)] })
  }
}

async function remove(b: S['MikrotikBackupItem']) {
  const ok = await ask(
    `Delete version ${b.number}?`,
    'The backup is gone for good. The versions before and after it stay.',
  )
  if (
    ok &&
    (await done(() => api.mikrotik.removeBackup(props.sensor.id, b.id), { success: `Version ${b.number} deleted` }))
  ) {
    if (shown.value) shown.value = null
    selected.value = selected.value.filter((s) => s !== b.id)
    await queryClient.invalidateQueries({ queryKey: [...keys.mikrotik, 'backups', props.sensor.id] })
  }
}

const triggers: Record<S['MikrotikBackupTrigger'], string> = {
  FirstLogin: 'first login',
  Schedule: 'scheduled',
  Manual: 'by hand',
  BeforeUpdate: 'before an update',
  BeforeCommand: 'before a command',
  AfterCommand: 'after a command',
  AfterUpdate: 'after an update',
}
const title = computed(() => {
  const s = shown.value
  if (!s) return ''
  if (s.kind === 'content') return `Version ${s.content.backup.number}`
  return s.diff.from ? `Version ${s.diff.from.number} → ${s.diff.to.number}` : `Version ${s.diff.to.number}, the first`
})
</script>

<template>
  <div class="tab">
    <div class="toolbar">
      <span class="muted">
        <template v-if="m.backupIntervalHours === null">Scheduled backups are off; Back up now still exports.</template>
        <template v-else
          >Exported as pnmt {{ scheduleLabel(m.backupIntervalHours).toLowerCase() }}; a version is stored only when the
          configuration changed.</template
        >
        <template v-if="m.lastExportAt"> Last export <RelativeTime :value="m.lastExportAt" />.</template>
      </span>
      <template v-if="canWrite && anyEncrypted">
        <template v-if="backupKey.loaded.value">
          <Tag value="key loaded" severity="success" icon="pi pi-key" />
          <Button label="Forget key" size="small" text severity="secondary" @click="clearBackupKey" />
        </template>
        <Button v-else label="Load key…" icon="pi pi-key" size="small" severity="secondary" @click="openKeyDialog" />
      </template>
      <Button
        v-if="canWrite && selected.length === 2"
        label="Compare the two"
        icon="pi pi-arrow-right-arrow-left"
        size="small"
        severity="secondary"
        @click="compareSelected"
      />
      <Button
        v-if="canWrite"
        label="Back up now"
        icon="pi pi-save"
        size="small"
        :loading="busy"
        class="new"
        @click="backUp"
      />
    </div>
    <Message v-if="m.lastBackupError" severity="error" :closable="false" size="small">
      The last export failed{{ m.backupFailures > 1 ? ` (${m.backupFailures} times in a row)` : '' }}:
      {{ m.lastBackupError }}
    </Message>

    <ListTable
      :table="table"
      what="backups"
      :empty-text="
        m.backupIntervalHours === null
          ? 'No backup yet: scheduled backups are off; Back up now takes one.'
          : 'No backup yet: the first is taken at the first login.'
      "
    >
      <Column v-if="canWrite" header="" style="width: 2.5rem">
        <template #body="{ data }">
          <Checkbox
            :model-value="selected.includes(data.id)"
            binary
            :disabled="!readable(data)"
            :aria-label="`Select version ${data.number} to compare`"
            @update:model-value="(v: boolean) => toggle(data.id, v)"
          />
        </template>
      </Column>
      <Column header="Version" style="width: 6rem">
        <template #body="{ data }">
          <span class="mono">v{{ data.number }}</span>
        </template>
      </Column>
      <Column header="Taken" style="width: 12rem">
        <template #body="{ data }">
          <span :title="formatAbsolute(data.createdAt)"><RelativeTime :value="data.createdAt" /></span>
          <div class="muted small">
            {{ triggers[data.trigger as S['MikrotikBackupTrigger']]
            }}<template v-if="data.createdBy"> by {{ data.createdBy }}</template>
          </div>
        </template>
      </Column>
      <Column header="RouterOS" style="width: 7rem">
        <template #body="{ data }"
          ><span class="mono">{{ data.routerOsVersion ?? '–' }}</span></template
        >
      </Column>
      <Column header="Changes">
        <template #body="{ data }">
          <span v-if="data.linesAdded === null" class="muted">the first</span>
          <span v-else class="counts"
            ><span class="added">+{{ data.linesAdded }}</span
            ><span class="removed">−{{ data.linesRemoved }}</span> lines</span
          >
          <span class="muted small"> · {{ data.lineCount }} lines, {{ formatBytes(data.size) }}</span>
          <Tag v-if="data.includesSecrets" value="secrets" severity="warn" class="badge" />
          <Tag
            v-if="data.encryptedWith"
            v-tooltip.top="`Encrypted with the backup key ${data.encryptedWith}`"
            value="encrypted"
            icon="pi pi-lock"
            severity="info"
            class="badge"
          />
          <Tag v-if="data.kind === 'Binary'" value="binary" severity="secondary" class="badge" />
        </template>
      </Column>
      <Column header="" style="width: 11rem">
        <template #body="{ data }">
          <span v-if="readable(data)" class="row-actions">
            <Button
              v-if="data.kind === 'Export'"
              v-tooltip.top="'View'"
              icon="pi pi-eye"
              text
              rounded
              size="small"
              severity="secondary"
              :aria-label="`View version ${data.number}`"
              @click="view(data)"
            />
            <Button
              v-if="data.kind === 'Export'"
              v-tooltip.top="'Changes'"
              icon="pi pi-arrow-right-arrow-left"
              text
              rounded
              size="small"
              severity="secondary"
              :aria-label="`Changes of version ${data.number}`"
              @click="changes(data)"
            />
            <Button
              v-if="data.encryptedWith && backupKey.loaded.value"
              v-tooltip.top="'Download, decrypted'"
              icon="pi pi-download"
              text
              rounded
              size="small"
              severity="secondary"
              :aria-label="`Download version ${data.number}, decrypted`"
              @click="downloadDecrypted(data)"
            />
            <a
              v-else
              v-tooltip.top="data.encryptedWith ? 'Download (encrypted, .age)' : 'Download'"
              class="p-button p-button-text p-button-rounded p-button-sm p-button-secondary p-button-icon-only"
              :href="api.mikrotik.downloadUrl(sensor.id, data.id)"
              :aria-label="`Download version ${data.number}`"
              ><i class="pi pi-download"
            /></a>
            <Button
              v-if="isAdmin"
              v-tooltip.top="'Delete'"
              icon="pi pi-trash"
              text
              rounded
              size="small"
              severity="secondary"
              :aria-label="`Delete version ${data.number}`"
              @click="remove(data)"
            />
          </span>
          <span v-else-if="data.includesSecrets" class="muted small">Admins only</span>
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <span class="mono">v{{ item.number }}</span>
          <RelativeTime :value="item.createdAt" />
          <span v-if="item.linesAdded !== null" class="muted">+{{ item.linesAdded }} −{{ item.linesRemoved }}</span>
          <Button v-if="readable(item)" label="Changes" size="small" text class="right" @click="changes(item)" />
        </div>
      </template>
    </ListTable>

    <section v-if="shown" class="panel" aria-labelledby="shown-title">
      <div id="shown-title" class="panel-header shown-header">
        {{ title }}
        <template v-if="shown.kind === 'diff'">
          <span class="added">+{{ shown.diff.added }}</span> <span class="removed">−{{ shown.diff.removed }}</span>
        </template>
        <span class="spacer" />
        <CopyField
          v-if="shown.kind === 'diff' && shown.diff.unified"
          :value="shown.diff.unified"
          label="Diff"
          display="Copy as diff -u"
        />
        <CopyField v-if="shown.kind === 'content'" :value="shown.content.content ?? ''" label="Export" display="Copy" />
        <Button
          icon="pi pi-times"
          text
          rounded
          size="small"
          severity="secondary"
          aria-label="Close"
          @click="shown = null"
        />
      </div>
      <div class="panel-body">
        <DiffView v-if="shown.kind === 'diff'" :diff="shown.diff" />
        <pre v-else class="export" aria-label="Export">{{ shown.content.content }}</pre>
      </div>
    </section>
    <LoadKeyDialog v-model:visible="loadingKey" :needs="neededKey" @loaded="keyLoaded" />
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
  flex-wrap: wrap;
}

.new {
  margin-left: auto;
}

.mono {
  font-family: var(--pnmt-mono);
}

.small {
  font-size: 0.857rem;
}

.added {
  color: var(--pnmt-ok);
  font-weight: 600;
}

.removed {
  color: var(--pnmt-fail);
  font-weight: 600;
}

.counts {
  display: inline-flex;
  gap: 0.3rem;
}

.badge {
  margin-left: var(--pnmt-space-2);
}

.row-actions {
  display: inline-flex;
  align-items: center;
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.right {
  margin-left: auto;
}

.shown-header {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.spacer {
  flex: 1;
}

.export {
  margin: 0;
  max-height: 32rem;
  overflow: auto;
  padding: var(--pnmt-space-2);
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  white-space: pre;
}
</style>
