<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import type { MenuItem } from 'primevue/menuitem'
import { computed, reactive, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { formatBytes } from '@/format/bytes'
import { formatDay } from '@/format'
import { useSession } from '@/stores/session'
import {
  asRequest,
  canFetch,
  choiceOf,
  chooseOptions,
  chosenSummary,
  fileLabel,
  fileState,
  FOLLOW,
  fromText,
  isVersion,
  proposedName,
  uploadNames,
} from './versions'

/**
 * Versions (docs/agent-update-plan.md §8.1): the version each platform's agents get (PNMT's own by default, one newer than
 * PNMT after a warning), and every version here or in GitHub's list with its four files, where they came from and the agents
 * running it: fetch, download, delete, and for admins upload. Nothing here reaches an agent: they update when their server's
 * admin runs the update.
 */
const session = useSession()
const queryClient = useQueryClient()
const { run, done, ask, notify, busy } = useActions()
const key = [...keys.agents, 'versions']
const fetching = (d: S['AgentVersionList'] | undefined) =>
  (d?.versions ?? []).some((v) => v.files.some((f) => f.state === 'Fetching'))
const versions = useQuery({
  queryKey: key,
  queryFn: () => api.agents.versions(),
  // While PNMT fetches a file, its state is read again.
  refetchInterval: (q) => (fetching(q.state.data) ? 3_000 : false),
})
const d = computed(() => versions.data.value ?? null)
const set = (result: S['AgentVersionList'] | undefined) => {
  if (result) queryClient.setQueryData(key, result)
}

// --- what the agents get ----------------------------------------------------------------------------
const picked = reactive<Record<S['AgentPlatform'], string>>({ Windows: FOLLOW, Linux: FOLLOW })
// Only a changed choice resets the selects: the list is read again every 3 s while a file is fetched.
for (const platform of ['Windows', 'Linux'] as const) {
  watch(
    () => (d.value ? choiceOf(platform === 'Windows' ? d.value.windows : d.value.linux) : undefined),
    (choice) => {
      if (choice !== undefined) picked[platform] = choice
    },
    { immediate: true },
  )
}
const rows = computed(() =>
  d.value
    ? (['Windows', 'Linux'] as const).map((platform) => ({
        platform,
        chosen: platform === 'Windows' ? d.value!.windows : d.value!.linux,
        options: chooseOptions(d.value!, platform),
      }))
    : [],
)
const changed = computed(() => rows.value.some((r) => picked[r.platform] !== choiceOf(r.chosen)))

async function save() {
  const body = { windows: asRequest(picked.Windows), linux: asRequest(picked.Linux), confirmNewer: false }
  try {
    busy.value = true
    set(await api.agents.chooseVersions(body))
    notify.success('Saved: the agents get these versions at their next update')
  } catch (e) {
    if (e instanceof ApiError && e.code === 'newer_than_pnmt') {
      if (await ask('Newer than PNMT', e.detail ?? 'This version is newer than PNMT.', 'Choose it')) {
        const result = await run(() => api.agents.chooseVersions({ ...body, confirmNewer: true }), {
          success: 'Saved: the agents get these versions at their next update',
        })
        set(result)
      }
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    busy.value = false
  }
}

// --- the versions ---------------------------------------------------------------------------------
async function refreshCatalog() {
  set(await run(() => api.agents.versions(true)))
}

async function fetchVersion(version: string, files: string[] | null = null) {
  set(await run(() => api.agents.fetchVersion(version, files), { success: `Fetching ${version} from its release` }))
}

async function remove(v: S['AgentVersionItem']) {
  if (
    !(await ask(
      `Delete ${v.version}?`,
      `Its files go from PNMT's download folder.${v.onGitHub ? ' They can be fetched again from GitHub.' : ''}`,
    ))
  )
    return
  if (await done(() => api.agents.deleteVersion(v.version), { success: `${v.version} deleted` }))
    await versions.refetch()
}

function menu(v: S['AgentVersionItem']): MenuItem[] {
  const items: MenuItem[] = []
  if (canFetch(v))
    items.push({
      label: 'Fetch the missing files',
      icon: 'pi pi-cloud-download',
      command: () => fetchVersion(v.version),
    })
  for (const f of v.files.filter((x) => x.state === 'Here')) {
    items.push({
      label: `Download ${f.name}`,
      icon: 'pi pi-download',
      url: api.agents.versionFileUrl(v.version, f.name),
    })
    items.push({
      label: `${f.name}.sha256`,
      icon: 'pi pi-file',
      url: api.agents.versionFileUrl(v.version, `${f.name}.sha256`),
    })
  }
  items.push({
    label: 'Delete',
    icon: 'pi pi-trash',
    class: 'danger',
    disabled: !!v.deleteRefused,
    command: () => remove(v),
  })
  return items
}

const fetchOpen = ref(false)
const fetchWhat = ref('')
const fetchError = ref<string | undefined>()
async function fetchTyped() {
  const version = fetchWhat.value.trim()
  if (!isVersion(version)) {
    fetchError.value = 'A version as 1.8.0 or 1.8.0-rc1.'
    return
  }
  fetchOpen.value = false
  await fetchVersion(version)
  fetchWhat.value = ''
}

// --- upload (admins) --------------------------------------------------------------------------------
const uploadOpen = ref(false)
const picker = ref<HTMLInputElement | null>(null)
const upload = ref<{ version: string; file: File | null; name: string | null; sha256: string }>({
  version: '',
  file: null,
  name: null,
  sha256: '',
})
const uploadErrors = ref<Record<string, string | undefined>>({})
const uploading = ref(false)
function startUpload(version = '') {
  upload.value = { version, file: null, name: null, sha256: '' }
  uploadErrors.value = {}
  uploadOpen.value = true
}
function onPick(e: Event) {
  const f = (e.target as HTMLInputElement).files?.[0]
  if (!f) return
  upload.value.file = f
  upload.value.name ??= proposedName(f.name)
}
async function sendUpload() {
  const u = upload.value
  uploadErrors.value = {
    version: isVersion(u.version.trim()) ? undefined : 'A version as 1.8.0 or 1.8.0-rc1.',
    file: u.file ? undefined : 'Choose the executable.',
    name: u.name ? undefined : 'Which of the four files it is.',
    sha256: !u.sha256.trim() || /^[0-9a-fA-F]{64}$/.test(u.sha256.trim()) ? undefined : '64 hex digits, or empty.',
  }
  if (Object.values(uploadErrors.value).some(Boolean)) return
  uploading.value = true
  try {
    set(
      await api.agents.uploadVersionFile(u.version.trim(), {
        file: u.file!,
        name: u.name!,
        sha256: u.sha256.trim() || null,
      }),
    )
    notify.success(`${u.name} of ${u.version.trim()} uploaded and checked`)
    uploadOpen.value = false
    if (picker.value) picker.value.value = ''
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      uploadErrors.value = {
        version: e.field('version'),
        file: e.field('file'),
        name: e.field('name'),
        sha256: e.field('sha256'),
      }
    } else {
      notify.error(e, 'Not uploaded')
    }
  } finally {
    uploading.value = false
  }
}
</script>

<template>
  <div class="tab">
    <p class="muted small">
      Which version the agents get, and the agents' files PNMT keeps. The agents update when their server's admin runs
      the update on the server; PNMT never starts one.
    </p>
    <ErrorState v-if="versions.isError.value" :error="versions.error.value" @retry="versions.refetch()" />
    <template v-if="d">
      <section class="panel">
        <div class="panel-header">What the agents get</div>
        <form class="panel-body choose" @submit.prevent="save">
          <template v-for="row in rows" :key="row.platform">
            <span :id="`choose-${row.platform}`" class="platform">{{ row.platform }}</span>
            <div class="choice">
              <Select
                v-model="picked[row.platform]"
                :aria-labelledby="`choose-${row.platform}`"
                :options="row.options"
                option-label="label"
                option-value="value"
                class="select"
              />
              <div class="tags">
                <Tag v-if="row.chosen.followsPnmt" value="follows PNMT" severity="secondary" />
                <Tag v-if="row.chosen.preRelease" value="pre-release" severity="info" />
                <Tag v-if="row.chosen.newerThanPnmt" value="newer than PNMT" severity="warn" />
                <span class="small muted">{{ chosenSummary(row.chosen) }}</span>
                <RouterLink
                  v-if="row.chosen.older"
                  :to="{
                    name: 'servers',
                    params: { tab: 'servers' },
                    query: { platform: row.platform, filter: 'outdated' },
                  }"
                  class="small"
                  >the older ones</RouterLink
                >
              </div>
            </div>
          </template>
          <div class="actions">
            <Button type="submit" label="Save" icon="pi pi-check" :disabled="!changed" :loading="busy" />
            <span class="small muted">
              Saving starts nothing: it changes what <code>pnmt-agent update</code> installs, what the install lines
              download, and which agents are outdated.
            </span>
          </div>
        </form>
        <Message v-if="d.windows.newerThanPnmt || d.linux.newerThanPnmt" severity="warn" :closable="false" size="small">
          A version newer than PNMT ({{ d.pnmtVersion }}) is chosen: its agents may report what this PNMT doesn't show
          yet. Update PNMT when you can.
        </Message>
        <Message v-if="!d.serveDownloads" severity="info" :closable="false" size="small">
          PNMT doesn't serve the agents' files (Agents__ServeDownloads is off): the agents' update can't reach them
          here.
        </Message>
      </section>

      <section class="panel">
        <div class="panel-header">
          Versions
          <span class="spacer" />
          <Button
            v-if="d.catalog.configured"
            label="Read GitHub's list again"
            icon="pi pi-refresh"
            size="small"
            text
            :loading="busy"
            @click="refreshCatalog"
          />
          <Button
            label="Fetch a version…"
            icon="pi pi-cloud-download"
            size="small"
            severity="secondary"
            @click="fetchOpen = true"
          />
          <Button
            v-if="session.isAdmin"
            label="Upload…"
            icon="pi pi-upload"
            size="small"
            severity="secondary"
            @click="startUpload()"
          />
        </div>
        <Message v-if="d.catalog.configured && !d.catalog.available" severity="info" :closable="false" size="small">
          GitHub's list of releases could not be read<template v-if="d.catalog.error">: {{ d.catalog.error }}</template
          >. The versions here remain; Fetch a version takes a version number.
        </Message>
        <DataTable :value="d.versions" size="small" data-key="version">
          <Column header="Version" style="width: 15rem">
            <template #body="{ data: v }">
              <span class="mono">{{ v.version }}</span>
              <div class="tags">
                <Tag v-if="v.isPnmtVersion" value="PNMT's" severity="secondary" />
                <Tag v-if="v.chosenForWindows" value="Windows get it" severity="success" />
                <Tag v-if="v.chosenForLinux" value="Linux get it" severity="success" />
                <Tag v-if="v.preRelease" value="pre-release" severity="info" />
                <Tag v-if="v.newerThanPnmt" value="newer than PNMT" severity="warn" />
              </div>
            </template>
          </Column>
          <Column header="From" style="width: 14rem">
            <template #body="{ data: v }">
              <span class="small">{{ fromText(v) }}</span>
              <div v-if="v.releasedAt" class="small muted">
                released {{ formatDay(v.releasedAt) }}
                <a v-if="v.releaseUrl" :href="v.releaseUrl" rel="noopener">release</a>
              </div>
              <div v-if="v.keptUntilDeleted" class="small muted">kept until deleted</div>
            </template>
          </Column>
          <Column header="Files">
            <template #body="{ data: v }">
              <div class="files">
                <span v-for="f in v.files" :key="f.name" class="file" :title="f.error ?? f.sha256 ?? f.name">
                  <span class="small">{{ fileLabel(f) }}</span>
                  <Tag :value="fileState(f).label" :severity="fileState(f).severity" />
                  <span v-if="f.size" class="small muted">{{ formatBytes(f.size) }}</span>
                  <span v-if="f.state === 'Here' && f.addedAt" class="small muted"
                    ><RelativeTime :value="f.addedAt"
                  /></span>
                </span>
              </div>
            </template>
          </Column>
          <Column header="Agents" style="width: 8rem">
            <template #body="{ data: v }">
              <span v-if="v.windowsAgents" class="small">Windows {{ v.windowsAgents }}</span>
              <span v-if="v.windowsAgents && v.linuxAgents" class="muted"> · </span>
              <span v-if="v.linuxAgents" class="small">Linux {{ v.linuxAgents }}</span>
              <span v-if="!v.windowsAgents && !v.linuxAgents" class="small muted">none</span>
            </template>
          </Column>
          <Column header="" style="width: 8rem">
            <template #body="{ data: v }">
              <Button
                v-if="canFetch(v)"
                label="Fetch"
                icon="pi pi-cloud-download"
                size="small"
                text
                :aria-label="`Fetch ${v.version}`"
                @click="fetchVersion(v.version)"
              />
              <MoreMenu :items="menu(v)" :label="`More for ${v.version}`" />
            </template>
          </Column>
        </DataTable>
        <div class="panel-body small muted note">
          The download folder is <span class="mono">{{ d.folder }}</span> ({{ formatBytes(d.folderBytes) }}). It keeps
          the chosen versions, PNMT's own, versions with an upload, and the newest {{ d.keepVersions }} others; older
          ones from GitHub are deleted and can be fetched again. A file is served only while it matches its .sha256.
        </div>
      </section>
    </template>
    <p v-else-if="versions.isPending.value" class="muted">Loading…</p>

    <Dialog
      v-model:visible="fetchOpen"
      header="Fetch a version"
      modal
      :style="{ width: '26rem' }"
      :breakpoints="{ '600px': '95vw' }"
    >
      <form class="form" novalidate @submit.prevent="fetchTyped">
        <div class="field">
          <label for="fetch-version">Version</label>
          <InputText
            id="fetch-version"
            v-model="fetchWhat"
            class="mono"
            placeholder="1.8.0"
            :invalid="!!fetchError"
            autofocus
          />
          <small v-if="fetchError" class="field-error">{{ fetchError }}</small>
          <small v-else class="field-hint"
            >Its files from the release sensor-{{ fetchWhat.trim() || '…' }}, checked against their .sha256.</small
          >
        </div>
      </form>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="fetchOpen = false" />
        <Button label="Fetch" icon="pi pi-cloud-download" @click="fetchTyped" />
      </template>
    </Dialog>

    <Dialog
      v-model:visible="uploadOpen"
      header="Upload an agent's file"
      modal
      :style="{ width: '32rem' }"
      :breakpoints="{ '600px': '95vw' }"
    >
      <form class="form" novalidate @submit.prevent="sendUpload">
        <Message severity="warn" :closable="false" size="small">
          The servers install an uploaded file at their next update, as root or SYSTEM. Upload only files the release
          built.
        </Message>
        <div class="field">
          <label for="upload-version">Version</label>
          <InputText
            id="upload-version"
            v-model="upload.version"
            class="mono"
            placeholder="1.8.0"
            :invalid="!!uploadErrors.version"
          />
          <small v-if="uploadErrors.version" class="field-error">{{ uploadErrors.version }}</small>
          <small v-else class="field-hint">What the file reports: an agent refuses a file whose version differs.</small>
        </div>
        <div class="field">
          <label for="upload-file">File</label>
          <input id="upload-file" ref="picker" type="file" @change="onPick" />
          <small v-if="uploadErrors.file" class="field-error">{{ uploadErrors.file }}</small>
          <small v-else-if="upload.file" class="field-hint">{{ formatBytes(upload.file.size) }}</small>
        </div>
        <div class="field">
          <span id="upload-name" class="label">It is</span>
          <Select
            v-model="upload.name"
            aria-labelledby="upload-name"
            :options="[...uploadNames]"
            placeholder="One of the four files"
            :invalid="!!uploadErrors.name"
          />
          <small v-if="uploadErrors.name" class="field-error">{{ uploadErrors.name }}</small>
        </div>
        <div class="field">
          <label for="upload-sha">SHA-256 <span class="muted">(optional)</span></label>
          <InputText id="upload-sha" v-model="upload.sha256" class="mono" :invalid="!!uploadErrors.sha256" />
          <small v-if="uploadErrors.sha256" class="field-error">{{ uploadErrors.sha256 }}</small>
          <small v-else class="field-hint"
            >From the release's .sha256: the upload is refused when the file doesn't match.</small
          >
        </div>
      </form>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="uploadOpen = false" />
        <Button label="Upload" icon="pi pi-upload" :loading="uploading" @click="sendUpload" />
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

.tab > p {
  margin: 0;
}

.choose {
  display: grid;
  grid-template-columns: 6rem 1fr;
  gap: var(--pnmt-space-2) var(--pnmt-space-3);
  align-items: start;
}

.platform {
  padding-top: var(--pnmt-space-2);
  font-weight: 600;
}

.choice {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.select {
  max-width: 26rem;
}

.actions {
  grid-column: 2;
  display: flex;
  gap: var(--pnmt-space-3);
  align-items: center;
  flex-wrap: wrap;
}

.tags {
  display: flex;
  gap: var(--pnmt-space-1);
  align-items: center;
  flex-wrap: wrap;
  margin-top: var(--pnmt-space-1);
}

.files {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
}

.file {
  display: flex;
  gap: var(--pnmt-space-1);
  align-items: center;
  flex-wrap: wrap;
}

.spacer {
  flex: 1;
}

.note {
  border-top: 1px solid var(--pnmt-border);
}

.small {
  font-size: 0.857rem;
}

.label {
  font-weight: 500;
}
</style>
