<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Textarea from 'primevue/textarea'
import { computed, ref } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useCopy } from '@/composables/useCopy'
import { formatBytes, toHex } from '@/format/bytes'
import OtaDialog from '@/views/sensors/udp/OtaDialog.vue'

/**
 * Firmware images (docs/udp-iot-plan.md §4.11, §6.6), Admin only: upload a .bin (drag and drop or
 * a picker) with a name, a version and notes; PNMTD stores it with its SHA-256 and does not parse
 * it (the magic is a hint only). Images in use by an open update cannot be deleted.
 */
const { run, ask, invalidate, notify } = useActions()
const copy = useCopy()
const images = useQuery({ queryKey: [...keys.udp, 'firmware'], queryFn: api.udp.firmware })

// --- upload -------------------------------------------------------------------------------------
const picker = ref<HTMLInputElement | null>(null)
const over = ref(false)
const file = ref<File | null>(null)
const magic = ref<string | null>(null)
const name = ref('')
const version = ref('')
const notes = ref('')
const busy = ref(false)
const errors = ref<Record<string, string | undefined>>({})
const uploaded = ref<S['UdpFirmwareUploaded'] | null>(null)

/** The same hint the server gives (§4.11): ESP32 images start with E9, MCUboot ones with 3D B8 F3 96. */
function magicHint(head: Uint8Array): string | null {
  if (head[0] === 0xe9) return 'Looks like an ESP32 image (E9).'
  if (head[0] === 0x3d && head[1] === 0xb8 && head[2] === 0xf3 && head[3] === 0x96)
    return 'Looks like an MCUboot image (3D B8 F3 96).'
  return head.length ? `Starts with ${toHex(head)}: no known image header (a hint only).` : null
}

async function choose(f: File | null | undefined) {
  if (!f) return
  file.value = f
  uploaded.value = null
  errors.value = {}
  // A file name such as pump-1.5.0.bin proposes the name and the version.
  const found = f.name.match(/(\d+\.\d+(?:\.\d+)?)/)
  if (!name.value) name.value = f.name.replace(/\.bin$/i, '')
  if (!version.value && found) version.value = found[1]
  magic.value = magicHint(new Uint8Array(await f.slice(0, 4).arrayBuffer()))
}

function onDrop(e: DragEvent) {
  over.value = false
  void choose(e.dataTransfer?.files?.[0])
}

function onPick(e: Event) {
  void choose((e.target as HTMLInputElement).files?.[0])
}

async function upload() {
  errors.value = {
    file: file.value ? undefined : 'Choose a .bin.',
    name: name.value.trim() ? undefined : 'A name, such as "Pump controller".',
    version: version.value.trim() ? undefined : 'The version the offer carries, such as 1.5.0.',
  }
  if (Object.values(errors.value).some(Boolean)) return
  busy.value = true
  try {
    uploaded.value = await api.udp.uploadFirmware({
      file: file.value!,
      name: name.value.trim(),
      version: version.value.trim(),
      notes: notes.value.trim() || null,
    })
    notify.success(`${uploaded.value.firmware.name} ${uploaded.value.firmware.version} uploaded`)
    await invalidate([keys.udp])
    file.value = null
    magic.value = null
    name.value = version.value = notes.value = ''
    if (picker.value) picker.value.value = ''
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = {
        file: e.field('file'),
        name: e.field('name'),
        version: e.field('version'),
        notes: e.field('notes'),
      }
    } else {
      notify.error(e, 'Not uploaded')
    }
  } finally {
    busy.value = false
  }
}

// --- the list ------------------------------------------------------------------------------------
const editing = ref<S['UdpFirmwareItem'] | null>(null)
const editOpen = computed({ get: () => editing.value !== null, set: (v) => !v && (editing.value = null) })
const edit = ref({ name: '', version: '', notes: '' })
const editErrors = ref<Record<string, string | undefined>>({})
function startEdit(f: S['UdpFirmwareItem']) {
  edit.value = { name: f.name, version: f.version, notes: f.notes ?? '' }
  editErrors.value = {}
  editing.value = f
}
async function saveEdit() {
  const f = editing.value!
  try {
    await api.udp.updateFirmware(f.id, {
      name: edit.value.name.trim(),
      version: edit.value.version.trim(),
      notes: edit.value.notes.trim() || null,
    })
    notify.success(`${edit.value.name} saved`)
    editing.value = null
    await invalidate([keys.udp])
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      editErrors.value = { name: e.field('name'), version: e.field('version'), notes: e.field('notes') }
    } else {
      notify.error(e, 'Not saved')
    }
  }
}

async function remove(f: S['UdpFirmwareItem']) {
  const ok = await ask(
    `Delete ${f.name} ${f.version}?`,
    f.jobs
      ? `${f.jobs} update${f.jobs === 1 ? '' : 's'} used it; their records stay, with the image's name, version and hash.`
      : 'The image is deleted. This cannot be undone.',
  )
  if (ok)
    await run(() => api.udp.removeFirmware(f.id), { success: `${f.name} ${f.version} deleted`, invalidate: [keys.udp] })
}

const updateWith = ref<string | null>(null)
const updateOpen = computed({ get: () => updateWith.value !== null, set: (v) => !v && (updateWith.value = null) })
</script>

<template>
  <div class="tab">
    <section class="panel" aria-labelledby="upload-title">
      <div id="upload-title" class="panel-header">Upload an image</div>
      <form class="panel-body upload" novalidate @submit.prevent="upload">
        <div
          class="drop"
          :class="{ over, invalid: !!errors.file }"
          role="button"
          tabindex="0"
          aria-label="Choose a firmware image"
          @click="picker?.click()"
          @keydown.enter.prevent="picker?.click()"
          @keydown.space.prevent="picker?.click()"
          @dragover.prevent="over = true"
          @dragleave="over = false"
          @drop.prevent="onDrop"
        >
          <i class="pi pi-upload" aria-hidden="true" />
          <div v-if="file">
            <strong>{{ file.name }}</strong> · {{ formatBytes(file.size) }}
            <div v-if="magic" class="muted small">{{ magic }}</div>
          </div>
          <div v-else>Drop a <code>.bin</code> here, or click to choose one. At most 16 MiB.</div>
          <input ref="picker" type="file" accept=".bin,application/octet-stream" class="hidden" @change="onPick" />
        </div>
        <small v-if="errors.file" class="field-error">{{ errors.file }}</small>
        <div class="grid">
          <div class="field">
            <label for="fw-name">Name</label>
            <InputText id="fw-name" v-model="name" placeholder="Pump controller" :invalid="!!errors.name" />
            <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
          </div>
          <div class="field">
            <label for="fw-version">Version</label>
            <InputText id="fw-version" v-model="version" class="mono" placeholder="1.5.0" :invalid="!!errors.version" />
            <small v-if="errors.version" class="field-error">{{ errors.version }}</small>
            <small v-else class="field-hint">Sent in the offer; a BOOT with this INFO counts as applied.</small>
          </div>
          <div class="field wide">
            <label for="fw-notes">Notes <span class="muted">(optional)</span></label>
            <Textarea id="fw-notes" v-model="notes" rows="2" auto-resize placeholder="Change log, target hardware" />
            <small v-if="errors.notes" class="field-error">{{ errors.notes }}</small>
          </div>
        </div>
        <div class="form-actions">
          <Button type="submit" label="Upload" icon="pi pi-upload" :loading="busy" />
        </div>
        <Message v-if="uploaded" severity="success" :closable="false">
          <strong>{{ uploaded.firmware.name }} {{ uploaded.firmware.version }}</strong
          >: {{ formatBytes(uploaded.firmware.size) }}, SHA-256
          <code class="hash">{{ uploaded.firmware.sha256 }}</code><template v-if="uploaded.firmware.magic"
            >, {{ uploaded.firmware.magic }}</template
          >
        </Message>
        <Message v-for="w in uploaded?.warnings ?? []" :key="w" severity="warn" :closable="false">{{ w }}</Message>
      </form>
    </section>

    <section class="panel" aria-labelledby="images-title">
      <div id="images-title" class="panel-header">Images</div>
      <ErrorState
        v-if="images.isError.value && !images.data.value"
        :error="images.error.value"
        what="the images"
        @retry="images.refetch()"
      />
      <EmptyState
        v-else-if="images.data.value && !images.data.value.length"
        icon="pi pi-box"
        title="No image uploaded yet."
      />
      <DataTable
        v-else
        :value="images.data.value ?? []"
        scrollable
        size="small"
        data-key="id"
        :loading="images.isLoading.value"
        class="images"
      >
        <Column header="Name">
          <template #body="{ data }">
            <div class="strong">{{ data.name }}</div>
            <div v-if="data.notes" class="muted small ellipsis notes" :title="data.notes">{{ data.notes }}</div>
          </template>
        </Column>
        <Column header="Version" style="width: 7rem">
          <template #body="{ data }"
            ><span class="mono">{{ data.version }}</span></template
          >
        </Column>
        <Column header="File">
          <template #body="{ data }">
            <div class="ellipsis file" :title="data.fileName">{{ data.fileName }}</div>
            <div class="muted small">
              {{ formatBytes(data.size) }}<template v-if="data.magic"> · {{ data.magic }}</template>
            </div>
          </template>
        </Column>
        <Column header="SHA-256" style="width: 10rem">
          <template #body="{ data }">
            <span class="nowrap">
              <code :title="data.sha256">{{ data.sha256.slice(0, 12) }}…</code>
              <Button
                icon="pi pi-copy"
                text
                rounded
                size="small"
                severity="secondary"
                :aria-label="`Copy the SHA-256 of ${data.name}`"
                @click="copy(data.sha256, 'SHA-256')"
              />
            </span>
          </template>
        </Column>
        <Column header="Uploaded" style="width: 9rem">
          <template #body="{ data }">
            <RelativeTime :value="data.uploadedAt" />
            <div v-if="data.uploadedBy" class="muted small">by {{ data.uploadedBy }}</div>
          </template>
        </Column>
        <Column header="Used" style="width: 8rem">
          <template #body="{ data }">
            <div class="small">{{ data.nodesRunning }} running it</div>
            <div class="muted small">{{ data.jobs }} updates, {{ data.openJobs }} open</div>
          </template>
        </Column>
        <Column header="" style="width: 10rem">
          <template #body="{ data }">
            <span class="row-actions">
              <Button
                v-tooltip.top="'Update nodes…'"
                icon="pi pi-send"
                text
                rounded
                size="small"
                :aria-label="`Update nodes with ${data.name} ${data.version}`"
                @click="updateWith = data.id"
              />
              <a
                v-tooltip.top="'Download'"
                :href="api.udp.firmwareUrl(data.id)"
                :download="data.fileName"
                class="icon-link"
                :aria-label="`Download ${data.fileName}`"
                ><i class="pi pi-download" aria-hidden="true"
              /></a>
              <Button
                v-tooltip.top="'Edit'"
                icon="pi pi-pencil"
                text
                rounded
                size="small"
                severity="secondary"
                :aria-label="`Edit ${data.name}`"
                @click="startEdit(data)"
              />
              <Button
                v-tooltip.top="data.openJobs ? 'An open update uses it' : 'Delete'"
                icon="pi pi-trash"
                text
                rounded
                size="small"
                severity="secondary"
                :aria-label="`Delete ${data.name} ${data.version}`"
                @click="remove(data)"
              />
            </span>
          </template>
        </Column>
      </DataTable>
    </section>

    <Dialog
      v-model:visible="editOpen"
      header="Edit image"
      modal
      :style="{ width: '30rem' }"
      :breakpoints="{ '600px': '95vw' }"
    >
      <form class="form" novalidate @submit.prevent="saveEdit">
        <div class="field">
          <label for="edit-name">Name</label>
          <InputText id="edit-name" v-model="edit.name" :invalid="!!editErrors.name" />
          <small v-if="editErrors.name" class="field-error">{{ editErrors.name }}</small>
        </div>
        <div class="field">
          <label for="edit-version">Version</label>
          <InputText id="edit-version" v-model="edit.version" class="mono" :invalid="!!editErrors.version" />
          <small v-if="editErrors.version" class="field-error">{{ editErrors.version }}</small>
          <small v-else class="field-hint">Open updates keep the version they offered.</small>
        </div>
        <div class="field">
          <label for="edit-notes">Notes</label>
          <Textarea id="edit-notes" v-model="edit.notes" rows="3" auto-resize />
        </div>
      </form>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="editing = null" />
        <Button label="Save" @click="saveEdit" />
      </template>
    </Dialog>
    <OtaDialog v-model:visible="updateOpen" :firmware-id="updateWith" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.upload {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  max-width: 48rem;
}

.drop {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
  padding: var(--pnmt-space-4);
  border: 2px dashed var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  cursor: pointer;
  background: var(--pnmt-bg);
}

.drop:hover,
.drop:focus-visible,
.drop.over {
  border-color: var(--p-primary-color);
  outline: none;
}

.drop.invalid {
  border-color: var(--p-red-500);
}

.drop > i {
  font-size: 1.5rem;
  color: var(--pnmt-muted);
}

.hidden {
  display: none;
}

.grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--pnmt-space-3) var(--pnmt-space-4);
}

.grid .wide {
  grid-column: 1 / -1;
}

.mono {
  font-family: var(--pnmt-mono);
}

.hash {
  overflow-wrap: anywhere;
}

.strong {
  font-weight: 500;
}

.small {
  font-size: 0.857rem;
}

.notes,
.file {
  max-width: 16rem;
}

.row-actions {
  display: inline-flex;
  align-items: center;
}

.icon-link {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 2rem;
  height: 2rem;
  color: var(--pnmt-muted);
  border-radius: 50%;
}

.icon-link:hover {
  background: var(--p-content-hover-background);
  text-decoration: none;
}

@media (max-width: 599px) {
  .grid {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
