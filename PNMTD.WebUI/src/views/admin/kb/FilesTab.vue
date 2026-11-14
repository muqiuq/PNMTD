<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Message from 'primevue/message'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, watch } from 'vue'
import { kbApi, kbKeys, kbNumber } from '@/api/kb'
import type { Schemas } from '@/api/client'
import ErrorState from '@/components/ErrorState.vue'
import { fileIcon, fileSize } from '@/components/markdown/files'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { formatAbsolute } from '@/format'
import { formatBytes } from '@/format/bytes'

/**
 * The files (§10.6, §5.5): the count and size per kind and the free space on the folder's disk; the File storage
 * sensor's last findings (missing or changed files, unknown ones on disk); the largest files with where they are used;
 * unused uploads and files waiting to leave the disk; deleting at once (FileDeleted).
 */
type FileAdminItem = Schemas['FileAdminItem']
const { done, ask } = useActions()

const views = [
  { label: 'Largest', value: 'largest' },
  { label: 'Unused', value: 'unused' },
  { label: 'Unknown on disk', value: 'unknown' },
]
const view = ref<'largest' | 'unused' | 'unknown'>('largest')
const kinds = [
  { label: 'Every kind', value: '' },
  { label: 'Images', value: 'Image' },
  { label: 'Attachments', value: 'Attachment' },
]
const kind = ref<'' | 'Image' | 'Attachment'>('')
const page = ref(1)
const PageSize = 50
watch([view, kind], () => (page.value = 1))

const params = computed(() => ({
  kind: kind.value || undefined,
  unused: view.value === 'unused' || undefined,
  unknown: view.value === 'unknown' || undefined,
  page: page.value,
  pageSize: PageSize,
  sort: '-size',
}))
const overview = useQuery({
  queryKey: computed(() => [...kbKeys.files, params.value]),
  queryFn: () => kbApi.files(params.value),
  placeholderData: keepPreviousData,
  refetchInterval: 60_000,
})
const data = computed(() => overview.data.value)
const storage = computed(() => data.value?.storage)
const lowSpace = computed(() => {
  const s = storage.value
  return !!s && s.freeBytes !== null && s.freeBytes !== undefined && s.freeBytes < s.minFreeBytes
})
const totalBytes = computed(() => (data.value?.totals ?? []).reduce((sum, t) => sum + t.bytes, 0))

async function remove(item: FileAdminItem) {
  const uses = item.usedBy.length
  if (
    await ask(
      `Delete ${item.file.name}?`,
      `It leaves the disk at once${uses ? `, and ${uses} page${uses === 1 ? '' : 's'} or location${uses === 1 ? '' : 's'} show File removed instead` : ''}. This cannot be undone.`,
    )
  ) {
    await done(() => kbApi.removeFile(item.file.id), {
      success: `${item.file.name} deleted`,
      invalidate: [kbKeys.files, kbKeys.all],
    })
  }
}

function useTarget(u: Schemas['FileUseRef']) {
  return u.kind === 'Page' && u.number !== null && u.number !== undefined
    ? { name: 'kb-page', params: { number: u.number } }
    : { name: 'location', params: { id: u.id } }
}
</script>

<template>
  <ErrorState
    v-if="overview.isError.value && !data"
    :error="overview.error.value"
    what="the files"
    @retry="overview.refetch()"
  />
  <template v-else-if="data">
    <div class="strip panel">
      <div v-for="t in data.totals" :key="t.kind" class="cell">
        <span class="label">{{ t.kind === 'Image' ? 'Images' : 'Attachments' }}</span>
        <span class="value">{{ t.count.toLocaleString() }}</span>
        <span class="muted small">{{ formatBytes(t.bytes) }}</span>
      </div>
      <div class="cell">
        <span class="label">In all</span>
        <span class="value">{{ formatBytes(totalBytes) }}</span>
        <span class="muted small mono ellipsis" :title="data.storage.path">{{ data.storage.path }}</span>
      </div>
      <div class="cell" :class="{ low: lowSpace || !data.storage.writable }">
        <span class="label">Free on the disk</span>
        <span class="value">{{ formatBytes(data.storage.freeBytes) }}</span>
        <span class="muted small">
          <template v-if="data.storage.totalBytes">of {{ formatBytes(data.storage.totalBytes) }} · </template>at least
          {{ formatBytes(data.storage.minFreeBytes) }}
        </span>
      </div>
    </div>
    <Message v-if="!data.storage.writable" severity="error" :closable="false">
      The folder can't be written: {{ data.storage.problem ?? 'unknown reason' }}.
    </Message>

    <section class="panel" aria-labelledby="kb-check-title">
      <div id="kb-check-title" class="panel-header">
        The File storage check
        <span v-if="data.check" class="muted check-when">
          {{ data.check.contentsChecked ? 'with contents' : 'sizes only' }} ·
          <RelativeTime :value="data.check.checkedAt" />
        </span>
      </div>
      <div class="panel-body">
        <p v-if="!data.check" class="muted none">Not checked since PNMTD started.</p>
        <template v-else>
          <p v-if="!data.check.problem && !data.check.unknownCount" class="muted none">
            <i class="pi pi-check-circle ok" aria-hidden="true" /> Every file is in the folder as stored.
          </p>
          <ul v-if="data.check.missing.length || data.check.changed.length" class="findings">
            <li v-for="f in data.check.missing" :key="`m-${f.id}`">
              <i class="pi pi-times-circle fail" aria-hidden="true" /> <strong>{{ f.name }}</strong> is missing
              <span class="muted mono">{{ f.path }}</span> <span class="muted">{{ f.detail }}</span>
            </li>
            <li v-for="f in data.check.changed" :key="`c-${f.id}`">
              <i class="pi pi-exclamation-triangle fail" aria-hidden="true" /> <strong>{{ f.name }}</strong> changed
              <span class="muted mono">{{ f.path }}</span> <span class="muted">{{ f.detail }}</span>
            </li>
          </ul>
          <p v-if="data.check.unknownCount" class="none">
            {{ data.check.unknownCount }} file{{ data.check.unknownCount === 1 ? '' : 's' }} on disk that PNMT doesn't
            know:
            <Button label="Show" text size="small" @click="view = 'unknown'" />
          </p>
        </template>
      </div>
    </section>

    <div class="controls">
      <SelectButton
        v-model="view"
        :options="views"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
      />
      <Select
        v-if="view !== 'unknown'"
        v-model="kind"
        :options="kinds"
        option-label="label"
        option-value="value"
        placeholder="Every kind"
        size="small"
        aria-label="Kind"
      />
    </div>

    <section v-if="view === 'unknown'" class="panel">
      <p class="muted unknown-note">Listed, never deleted: someone may have put them there on purpose.</p>
      <DataTable :value="data.unknown ?? []" size="small" data-key="path">
        <Column field="path" header="Path"
          ><template #body="{ data: u }"
            ><span class="mono">{{ u.path }}</span></template
          ></Column
        >
        <Column field="size" header="Size" style="width: 7rem"
          ><template #body="{ data: u }">{{ fileSize(u.size) }}</template></Column
        >
        <Column field="modifiedAt" header="Changed" style="width: 10rem">
          <template #body="{ data: u }"><RelativeTime :value="u.modifiedAt" /></template>
        </Column>
      </DataTable>
    </section>
    <section v-else class="panel">
      <DataTable
        :value="data.files.items"
        size="small"
        data-key="file.id"
        lazy
        :paginator="data.files.totalCount > PageSize"
        :rows="PageSize"
        :total-records="data.files.totalCount"
        :first="(page - 1) * PageSize"
        @page="(e) => (page = e.page + 1)"
      >
        <template #empty><span class="muted">No files.</span></template>
        <Column header="File">
          <template #body="{ data: f }">
            <span class="file">
              <i :class="fileIcon(f.file.name, f.file.contentType)" class="muted" aria-hidden="true" />
              <a
                :href="f.file.url"
                :download="f.file.kind === 'Attachment' ? '' : undefined"
                target="_blank"
                rel="noopener"
                >{{ f.file.name }}</a
              >
            </span>
          </template>
        </Column>
        <Column header="Size" style="width: 7rem"
          ><template #body="{ data: f }">{{ fileSize(f.file.size) }}</template></Column
        >
        <Column header="Used by">
          <template #body="{ data: f }">
            <span v-if="!f.usedBy.length" class="muted">
              <template v-if="f.deletedAt">Leaves the disk {{ formatAbsolute(f.leavesDiskAt) }}</template>
              <template v-else>Not used</template>
            </span>
            <span v-else class="uses">
              <RouterLink v-for="u in f.usedBy" :key="u.id" :to="useTarget(u)">
                {{ u.kind === 'Page' && u.number ? `${kbNumber(u.number)} ${u.name}` : u.name }}
              </RouterLink>
            </span>
          </template>
        </Column>
        <Column header="Uploaded" style="width: 11rem">
          <template #body="{ data: f }">
            <RelativeTime :value="f.createdAt" /><span v-if="f.createdByName" class="muted">
              · {{ f.createdByName }}</span
            >
          </template>
        </Column>
        <Column header="" style="width: 3rem">
          <template #body="{ data: f }">
            <Button
              icon="pi pi-trash"
              text
              rounded
              size="small"
              severity="danger"
              :aria-label="`Delete ${f.file.name}`"
              @click="remove(f)"
            />
          </template>
        </Column>
      </DataTable>
    </section>
  </template>
</template>

<style scoped>
.strip {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr));
  gap: var(--pnmt-space-1);
  padding: var(--pnmt-space-2);
}

.cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-radius: var(--p-border-radius-md);
  min-width: 0;
}

.cell.low {
  background: var(--pnmt-fail-soft);
}

.label {
  font-size: 0.786rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pnmt-muted);
}

.value {
  font-size: 1.4rem;
  font-weight: 600;
  line-height: 1.2;
}

.small {
  font-size: 0.857rem;
}

.check-when {
  margin-left: auto;
  font-weight: 400;
  font-size: 0.857rem;
}

.none {
  margin: 0;
}

.findings {
  margin: 0;
  padding-left: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.ok {
  color: var(--pnmt-ok);
}

.fail {
  color: var(--pnmt-fail);
}

.controls {
  display: flex;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
  align-items: center;
}

.unknown-note {
  margin: 0;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
}

.file {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.uses {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
}
</style>
