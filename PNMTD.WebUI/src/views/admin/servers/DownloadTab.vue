<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { api, keys, type S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { bytes } from './agentFormat'
import { fileStateLabel, fileStateSeverity } from './installLine'
import LinuxInstallLine from './LinuxInstallLine.vue'

/**
 * Download (docs/windows-sensor-plan.md §10.1; docs/linux-sensor-plan.md §4.4, §11.1): PNMTD's version; the Windows agent for
 * x64 and ARM64 from PNMT and from GitHub, with the SHA-256 and the line that checks it, and the command line for unattended
 * installs; the Linux agent's install and update lines for x64 and arm64, from PNMT or GitHub, with wget or curl; and each
 * file's state in PNMT's download folder (here, being fetched, failed with the reason, not fetched) with Fetch again. Admin.
 */
const queryClient = useQueryClient()
const key = [...keys.agents, 'downloads']
const fetching = (d: S['AgentDownloadLinks'] | undefined) => (d?.assets ?? []).some((a) => a.state === 'Fetching')
const agent = useQuery({
  queryKey: key,
  queryFn: api.agents.downloads,
  staleTime: 600_000,
  // While PNMT fetches a file, its state is read again.
  refetchInterval: (q) => (fetching(q.state.data) ? 3_000 : false),
})
const d = computed(() => agent.data.value ?? null)
// Plain http:// works after a confirmation: on the command line, --allow-http.
const allowHttp = computed(() => (d.value?.url.startsWith('http://') ? ' --allow-http' : ''))
const anyMissing = computed(() => (d.value?.assets ?? []).some((a) => a.state !== 'Here' && a.state !== 'Fetching'))

const { run, busy } = useActions()
async function fetchAgain(files: string[] | null) {
  const result = await run(() => api.agents.fetchDownloads(files), {
    success: files?.length === 1 ? `Fetching ${files[0]}` : 'Fetching the missing files',
  })
  if (result) queryClient.setQueryData(key, result)
}
</script>

<template>
  <div class="tab">
    <template v-if="d">
      <p class="muted small">
        The agents of PNMTD {{ d.version }}: PNMT serves them from its download folder, fetched from the release on
        GitHub and checked against its SHA-256.
        <a v-if="d.releaseUrl" :href="d.releaseUrl" rel="noopener">The release on GitHub</a>
      </p>
      <Message v-if="!d.serveDownloads" severity="info" :closable="false" size="small">
        PNMT doesn't serve the agents' files (Agents__ServeDownloads is off): the links and lines are GitHub's.
      </Message>
      <Message v-if="d.note" severity="info" :closable="false" size="small">{{ d.note }}</Message>
      <div class="two-columns">
        <section class="panel">
          <div class="panel-header">Windows: PNMT Sensor {{ d.version }}</div>
          <div class="panel-body content">
            <p>
              One executable with .NET included, for Windows Server 2016–2025, Windows 11 and Windows 10 Pro, Enterprise
              and LTSC. Run it on the server as an administrator.
            </p>
            <div v-for="f in d.files" :key="f.architecture" class="file">
              <span class="links">
                <a :href="f.pnmtUrl ?? f.url" rel="noopener"><i class="pi pi-download" /> {{ f.fileName }}</a>
                <a v-if="f.pnmtUrl" :href="f.url" rel="noopener" class="small">from GitHub</a>
              </span>
              <span class="muted small">{{
                f.architecture === 'x64' ? 'Intel and AMD; also runs on ARM64' : 'ARM64, native'
              }}</span>
              <CopyField v-if="f.sha256" :value="f.sha256" :label="`SHA-256 of ${f.fileName}`" />
            </div>
            <h3>Check it</h3>
            <p class="small">
              The executable is not code-signed. Compare its SHA-256 in PowerShell; SmartScreen asks once (<em
                >More info → Run anyway</em
              >), and <code>Unblock-File</code> removes the mark for scripts.
            </p>
            <CopyField value="Get-FileHash .\pnmt-sensor-x64.exe -Algorithm SHA256" label="PowerShell" />
            <h3>Unattended (GPO, Intune, RMM)</h3>
            <p class="small">With a token from <em>Add server</em>; exit code 0 once connected.</p>
            <CopyField
              :value="`pnmt-sensor-x64.exe join --url ${d.url} --token ${d.tokenPlaceholder}${allowHttp} --quiet`"
              label="Command line"
            />
            <h3>Updating</h3>
            <p class="small">
              Run the newer executable on the server: its window offers <em>Update</em>, or
              <code>pnmt-sensor.exe update</code>. The identity and key stay.
            </p>
          </div>
        </section>
        <section class="panel">
          <div class="panel-header">Linux: the PNMT agent {{ d.version }}</div>
          <div class="panel-body content">
            <p>
              One binary with .NET included, for Debian 12 and 13, Ubuntu LTS 22.04 to 26.04 and Proxmox VE 8 and 9, x64
              and arm64, with systemd and glibc. One line installs and joins it, as root.
            </p>
            <h3>Install and join</h3>
            <p class="small">
              With a token from <em>Add server</em>, which fills it in: here it is
              <code>{{ d.tokenPlaceholder }}</code>.
            </p>
            <LinuxInstallLine :downloads="d" />
            <h3>Updating</h3>
            <p class="small">
              On the server, as root: it downloads the newer agent from PNMT, checks it against PNMT's signature, and
              restarts the service. The identity and key stay.
            </p>
            <CopyField :value="d.updateLine" label="Update line" />
          </div>
        </section>
      </div>
      <section class="panel">
        <div class="panel-header">
          PNMT's download folder
          <span class="spacer" />
          <Button
            v-if="d.serveDownloads && anyMissing"
            label="Fetch the missing files"
            icon="pi pi-cloud-download"
            size="small"
            severity="secondary"
            :loading="busy"
            @click="fetchAgain(null)"
          />
        </div>
        <DataTable :value="d.assets" size="small" data-key="name">
          <Column header="File">
            <template #body="{ data: f }">
              <span class="mono">{{ f.name }}</span>
              <div class="muted small">{{ f.platform }} · {{ f.architecture }}</div>
            </template>
          </Column>
          <Column header="State" style="width: 18rem">
            <template #body="{ data: f }">
              <Tag :value="fileStateLabel(f.state)" :severity="fileStateSeverity(f.state)" />
              <span v-if="f.fetchedAt" class="muted small"> <RelativeTime :value="f.fetchedAt" /></span>
              <div v-if="f.error" class="small fail">{{ f.error }}</div>
            </template>
          </Column>
          <Column header="Size" style="width: 7rem">
            <template #body="{ data: f }"
              ><span class="small">{{ f.size ? bytes(f.size) : '–' }}</span></template
            >
          </Column>
          <Column header="SHA-256" style="width: 12rem">
            <template #body="{ data: f }">
              <span v-if="f.sha256" class="mono small" :title="f.sha256">{{ f.sha256.slice(0, 12) }}…</span>
              <span v-else class="muted small">not known</span>
            </template>
          </Column>
          <Column header="Links" style="width: 9rem">
            <template #body="{ data: f }">
              <a v-if="f.pnmtUrl && f.state === 'Here'" :href="f.pnmtUrl" rel="noopener" class="small">PNMT</a>
              <span v-if="f.pnmtUrl && f.state === 'Here'" class="muted"> · </span>
              <a :href="f.gitHubUrl" rel="noopener" class="small">GitHub</a>
            </template>
          </Column>
          <Column v-if="d.serveDownloads" header="" style="width: 9rem">
            <template #body="{ data: f }">
              <Button
                label="Fetch again"
                icon="pi pi-refresh"
                size="small"
                text
                :disabled="f.state === 'Fetching'"
                :aria-label="`Fetch ${f.name} again`"
                @click="fetchAgain([f.name])"
              />
            </template>
          </Column>
        </DataTable>
        <div class="panel-body small muted note">
          A file is fetched from the release when first asked for, checked against its .sha256 and kept; a container
          starts empty. Fetch again replaces a file that doesn't match. Without internet, mount a folder holding the
          release's files at Agents__DownloadFolder.
        </div>
      </section>
    </template>
    <p v-else-if="agent.isPending.value" class="muted">Loading…</p>
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

.content {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.content p {
  margin: 0;
}

.file {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.links {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: baseline;
  flex-wrap: wrap;
}

.spacer {
  flex: 1;
}

.note {
  border-top: 1px solid var(--pnmt-border);
}

.fail {
  color: var(--pnmt-fail);
}

h3 {
  margin-top: var(--pnmt-space-2);
}

.small {
  font-size: 0.857rem;
}
</style>
