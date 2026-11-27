<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import ProgressBar from 'primevue/progressbar'
import { computed, ref } from 'vue'
import {
  chargeKindLabels,
  documentKindLabels,
  licenseKeys,
  licensesApi,
  money,
  uploadDocument,
  type LicenseDetail,
  type LicenseDocument,
  type LicenseDocumentKind,
} from '@/api/licenses'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import InlineField from '@/components/InlineField.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { fileSize } from '@/components/markdown/files'
import { useActions } from '@/composables/useActions'
import { formatDay } from '@/format'

/**
 * The item's *Documents* tab (docs/licences-plan.md §7.1): name, kind, description, size, added by and when, and the charge or
 * renewal each belongs to. Added by the button or by dropping files on the tab, several at once, with their progress; the kind is
 * suggested from the name. A download (Details) goes through the item only, audited, never cached; without Details the names show
 * without a link. Kind, description, charge and renewal are changed in place, a document removed (Edit).
 */
const props = defineProps<{ item: LicenseDetail; edit: boolean; details: boolean }>()
const queryClient = useQueryClient()
const { done, ask, notify } = useActions()

const documents = useQuery({
  queryKey: computed(() => licenseKeys.documents(props.item.id)),
  queryFn: () => licensesApi.documents(props.item.id),
})
const charges = useQuery({
  queryKey: computed(() => licenseKeys.itemCharges(props.item.id)),
  queryFn: () => licensesApi.itemCharges(props.item.id),
})
const renewals = useQuery({
  queryKey: computed(() => licenseKeys.renewals(props.item.id)),
  queryFn: () => licensesApi.renewals(props.item.id),
})
const rows = computed(() => [...(documents.data.value ?? [])].sort((a, b) => b.addedAt.localeCompare(a.addedAt)))

const kindOptions = (Object.keys(documentKindLabels) as LicenseDocumentKind[]).map((k) => ({
  label: documentKindLabels[k],
  value: k,
}))
const NONE = 'none'
const chargeOptions = computed(() => [
  { label: 'none', value: NONE },
  ...[...(charges.data.value ?? [])]
    .sort((a, b) => b.on.localeCompare(a.on))
    .map((c) => ({
      label: `${formatDay(c.on)} · ${chargeKindLabels[c.kind]} · ${money(c.amount, c.currency)}${c.reference ? ` · ${c.reference}` : ''}`,
      value: c.id,
    })),
])
const renewalOptions = computed(() => [
  { label: 'none', value: NONE },
  ...[...(renewals.data.value ?? [])]
    .sort((a, b) => b.on.localeCompare(a.on))
    .map((r) => ({ label: `${formatDay(r.on)}: until ${formatDay(r.newExpiresOn)}`, value: r.id })),
])

/** A document's change: the whole of it, as the API takes it (null clears a charge or renewal). */
async function change(
  d: LicenseDocument,
  patch: Partial<Pick<LicenseDocument, 'kind' | 'description' | 'chargeId' | 'renewalId'>>,
) {
  await licensesApi.updateDocument(props.item.id, d.fileId, {
    kind: patch.kind ?? d.kind,
    description: patch.description !== undefined ? patch.description : (d.description ?? null),
    chargeId: patch.chargeId !== undefined ? patch.chargeId : (d.chargeId ?? null),
    renewalId: patch.renewalId !== undefined ? patch.renewalId : (d.renewalId ?? null),
  })
  await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
}
const pickId = (v: unknown) => (v === NONE || v === null || v === undefined ? null : String(v))
const text = (v: unknown) => (v === null || v === undefined ? null : String(v).trim() || null)

async function remove(d: LicenseDocument) {
  if (
    !(await ask(
      `Remove ${d.name}?`,
      `It is no longer kept with ${props.item.display}; the audit log keeps that it was.`,
      'Remove',
    ))
  )
    return
  await done(() => licensesApi.removeDocument(props.item.id, d.fileId), {
    success: `${d.name} removed`,
    failure: 'Not removed',
    invalidate: [licenseKeys.all],
  })
}

// --- adding ---------------------------------------------------------------------------------------------------------------------
interface Upload {
  key: number
  name: string
  progress: number
  error: string | null
}
const uploads = ref<Upload[]>([])
const input = ref<HTMLInputElement | null>(null)
const dragging = ref(false)
let next = 0

async function add(files: FileList | File[] | null | undefined) {
  const list = Array.from(files ?? [])
  if (!list.length || !props.edit) return
  const mine = list.map((f) => ({ key: next++, name: f.name, progress: 0, error: null as string | null }))
  uploads.value = [...uploads.value, ...mine]
  let added = 0
  for (const [i, file] of list.entries()) {
    const row = uploads.value.find((u) => u.key === mine[i].key)!
    try {
      await uploadDocument(
        props.item.id,
        file,
        file.name,
        {},
        (fraction) => (row.progress = Math.round(fraction * 100)),
      )
      row.progress = 100
      added++
    } catch (e) {
      row.error = e instanceof Error ? e.message : String(e)
    }
  }
  await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
  if (added)
    notify.success(
      added === 1 ? `${list.length === 1 ? list[0].name : 'A document'} added` : `${added} documents added`,
    )
  // The finished ones go; the failed ones stay with their reason until dismissed.
  uploads.value = uploads.value.filter((u) => !mine.some((m) => m.key === u.key) || u.error)
}
function picked(event: Event) {
  const target = event.target as HTMLInputElement
  void add(target.files)
  target.value = ''
}
function onDrop(event: DragEvent) {
  dragging.value = false
  void add(event.dataTransfer?.files)
}
function onDragOver(event: DragEvent) {
  if (!props.edit || !event.dataTransfer?.types.includes('Files')) return
  event.preventDefault()
  dragging.value = true
}
</script>

<template>
  <section
    class="panel documents"
    :class="{ dragging }"
    aria-labelledby="documents-title"
    @dragover="onDragOver"
    @dragleave.self="dragging = false"
    @drop.prevent="onDrop"
  >
    <div class="panel-header">
      <span id="documents-title">Documents</span>
      <span class="spacer" />
      <template v-if="edit">
        <span class="muted hint">or drop files here</span>
        <Button label="Add…" icon="pi pi-upload" size="small" text data-test="add-documents" @click="input?.click()" />
        <input ref="input" type="file" multiple class="hidden" aria-label="Documents to add" @change="picked" />
      </template>
    </div>
    <ul v-if="uploads.length" class="uploads" data-test="uploads">
      <li v-for="u in uploads" :key="u.key">
        <span class="ellipsis name">{{ u.name }}</span>
        <template v-if="u.error">
          <span class="field-error">{{ u.error }}</span>
          <Button
            icon="pi pi-times"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`Dismiss ${u.name}`"
            @click="uploads = uploads.filter((x) => x.key !== u.key)"
          />
        </template>
        <ProgressBar v-else :value="u.progress" class="progress" />
      </li>
    </ul>
    <ErrorState
      v-if="documents.isError.value"
      :error="documents.error.value"
      what="the documents"
      @retry="documents.refetch()"
    />
    <EmptyState
      v-else-if="documents.data.value && !rows.length"
      icon="pi pi-file"
      title="No documents yet."
      :text="edit ? 'Invoices, contracts, quotes, licence and key files: add them, or drop them here.' : undefined"
    />
    <DataTable
      v-else
      :value="rows"
      :loading="documents.isPending.value"
      data-key="fileId"
      size="small"
      data-test="documents"
    >
      <Column header="Name">
        <template #body="{ data }">
          <a v-if="details" :href="licensesApi.documentUrl(item.id, data.fileId, data.name)" download class="name-cell"
            ><i class="pi pi-file" aria-hidden="true" /> {{ data.name }}</a
          >
          <span v-else class="name-cell" title="Downloading needs Licences &amp; services: Details"
            ><i class="pi pi-file muted" aria-hidden="true" /> {{ data.name }}</span
          >
        </template>
      </Column>
      <Column header="Kind" style="width: 10rem">
        <template #body="{ data }">
          <InlineField
            :value="data.kind"
            type="enum"
            :options="kindOptions"
            label="Kind"
            field="kind"
            :readonly="!edit"
            :save="(v) => change(data, { kind: v as LicenseDocumentKind })"
          />
        </template>
      </Column>
      <Column header="Description">
        <template #body="{ data }">
          <InlineField
            :value="data.description"
            label="Description"
            field="description"
            :readonly="!edit"
            :save="(v) => change(data, { description: text(v) })"
          />
        </template>
      </Column>
      <Column header="Size" style="width: 6rem">
        <template #body="{ data }"
          ><span class="nowrap muted">{{ fileSize(data.size) }}</span></template
        >
      </Column>
      <Column header="Added">
        <template #body="{ data }">
          <span class="nowrap"
            ><RelativeTime :value="data.addedAt" /><span v-if="data.addedBy" class="muted">
              by {{ data.addedBy }}</span
            ></span
          >
        </template>
      </Column>
      <Column header="Charge">
        <template #body="{ data }">
          <InlineField
            :value="data.chargeId ?? NONE"
            type="enum"
            :options="chargeOptions"
            label="Charge"
            field="chargeId"
            :readonly="!edit"
            :save="(v) => change(data, { chargeId: pickId(v) })"
          />
        </template>
      </Column>
      <Column header="Renewal">
        <template #body="{ data }">
          <InlineField
            :value="data.renewalId ?? NONE"
            type="enum"
            :options="renewalOptions"
            label="Renewal"
            field="renewalId"
            :readonly="!edit"
            :save="(v) => change(data, { renewalId: pickId(v) })"
          />
        </template>
      </Column>
      <Column v-if="edit" style="width: 3rem">
        <template #body="{ data }">
          <Button
            icon="pi pi-trash"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`Remove ${data.name}`"
            @click="remove(data)"
          />
        </template>
      </Column>
    </DataTable>
    <p v-if="!details && rows.length" class="muted note">
      Downloading the documents needs Licences &amp; services: Details.
    </p>
  </section>
</template>

<style scoped>
.documents {
  position: relative;
}

.documents.dragging {
  outline: 2px dashed var(--p-primary-color);
  outline-offset: -2px;
}

.spacer {
  flex: 1;
}

.hint {
  font-weight: 400;
  font-size: 0.857rem;
}

.hidden {
  display: none;
}

.uploads {
  list-style: none;
  margin: 0;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  display: grid;
  gap: var(--pnmt-space-1);
  border-bottom: 1px solid var(--pnmt-border);
}

.uploads li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.uploads .name {
  flex: 0 1 16rem;
}

.progress {
  flex: 1;
  height: 0.5rem;
}

.name-cell {
  overflow-wrap: anywhere;
}

.note {
  margin: 0;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  font-size: 0.857rem;
}
</style>
