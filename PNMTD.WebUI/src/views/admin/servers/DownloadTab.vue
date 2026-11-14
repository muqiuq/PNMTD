<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Message from 'primevue/message'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'

/**
 * Download (docs/windows-sensor-plan.md §10.1, §11): the Windows agent of PNMTD's own version from its GitHub release (the
 * tag sensor-{version}), for x64 and ARM64, with the SHA-256 and the line that checks it, and the command line for
 * unattended installs. The Linux agent's lines join it with its release (docs/linux-sensor-plan.md §11.1, L3).
 */
const agent = useQuery({ queryKey: [...keys.agents, 'downloads'], queryFn: api.agents.downloads, staleTime: 600_000 })
const url = computed(() => (typeof location !== 'undefined' ? location.origin : 'https://pnmt.example.com'))
// Plain http:// works after a confirmation: on the command line, --allow-http.
const allowHttp = computed(() => (url.value.startsWith('http://') ? ' --allow-http' : ''))
</script>

<template>
  <div class="tab">
    <section v-if="agent.data.value" class="panel">
      <div class="panel-header">Windows: PNMT Sensor {{ agent.data.value.version }}</div>
      <div class="panel-body content">
        <p>
          One executable with .NET included, for Windows Server 2016–2025, Windows 11 and Windows 10 Pro, Enterprise and
          LTSC. Run it on the server as an administrator.
        </p>
        <div v-for="f in agent.data.value.files" :key="f.architecture" class="file">
          <a :href="f.url" rel="noopener"><i class="pi pi-download" /> {{ f.fileName }}</a>
          <span class="muted small">{{
            f.architecture === 'x64' ? 'Intel and AMD; also runs on ARM64' : 'ARM64, native'
          }}</span>
          <CopyField v-if="f.sha256" :value="f.sha256" :label="`SHA-256 of ${f.fileName}`" />
        </div>
        <Message v-if="agent.data.value.note" severity="info" :closable="false" size="small">{{
          agent.data.value.note
        }}</Message>
        <p v-if="agent.data.value.releaseUrl">
          <a :href="agent.data.value.releaseUrl" rel="noopener">The release on GitHub</a>
        </p>
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
          :value="`pnmt-sensor-x64.exe join --url ${url} --token xxxx-xxxx${allowHttp} --quiet`"
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
      <div class="panel-header">Linux: the PNMT agent</div>
      <div class="panel-body">
        <p class="muted small">
          The agent for Debian comes with its release: its install and update lines will be here, from PNMT and from
          GitHub. Join tokens for Linux servers can be made already.
        </p>
      </div>
    </section>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.content {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.file {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

h3 {
  margin-top: var(--pnmt-space-2);
}

.small {
  font-size: 0.857rem;
}
</style>
