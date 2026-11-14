<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Message from 'primevue/message'
import { computed } from 'vue'
import { api, keys, type S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import LinuxInstallLine from './LinuxInstallLine.vue'

/**
 * A new join token (docs/windows-sensor-plan.md §3.3; docs/linux-sensor-plan.md §4.4, §11.1), shown once: the token in large
 * type, PNMT's URL and key; for Windows the downloads from PNMT and GitHub with their SHA-256, the command line for unattended
 * installs and what to do on the server; for Linux the one line with its toggles and what join will do. PNMTD starts fetching
 * the Linux files with the token, so their state is read again while one is being fetched.
 */
const props = defineProps<{ created: S['AgentJoinTokenCreated'] }>()
const linux = computed(() => props.created.item.platform === 'Linux')
const fetching = (d: S['AgentDownloadLinks'] | undefined) => (d?.assets ?? []).some((a) => a.state === 'Fetching')
const live = useQuery({
  queryKey: computed(() => [...keys.agents, 'downloads', 'token', props.created.item.id]),
  queryFn: api.agents.downloads,
  initialData: () => props.created.downloads,
  // Read again only while a file is being fetched.
  staleTime: Infinity,
  enabled: linux,
  refetchInterval: (q) => (fetching(q.state.data) ? 3_000 : false),
})
const downloads = computed(() => live.data.value ?? props.created.downloads)
</script>

<template>
  <div class="created">
    <Message severity="warn" :closable="false" size="small">
      The token is shown only now: PNMT keeps its hash. It is valid once, until
      {{ new Date(created.item.expiresAt).toLocaleString() }}.
    </Message>
    <Message v-if="created.url.startsWith('http://')" severity="warn" :closable="false" size="small">
      PNMT is reached over plain HTTP: not encrypted. The agent asks to confirm it, and shows PNMT's key: it must be
      <strong class="mono">{{ created.pnmtdFingerprint }}</strong>.
    </Message>
    <div class="token mono" aria-label="Join token">{{ created.token }}</div>
    <CopyField :value="created.token" label="Join token" />
    <dl class="facts">
      <dt>PNMT's URL</dt>
      <dd><CopyField :value="created.url" label="PNMT's URL" /></dd>
      <dt>PNMT's key</dt>
      <dd class="mono small">{{ created.pnmtdFingerprint }}</dd>
      <dt>Platform</dt>
      <dd>{{ created.item.platform }}</dd>
      <dt>Host</dt>
      <dd>
        {{ created.item.host.name
        }}<template v-if="created.item.rejoin"> · re-join of {{ created.item.rejoin.name }}</template>
      </dd>
    </dl>
    <template v-if="linux">
      <h3>On the server</h3>
      <p class="small">
        One line, as root: it downloads the agent of PNMT {{ downloads.version }}, checks its SHA-256 before anything
        runs, installs it and joins.
      </p>
      <LinuxInstallLine :downloads="downloads" :token="created.token" />
      <h3>What join will do</h3>
      <ul class="steps small">
        <li>Check the machine (root, systemd, the distribution, a writable /var/lib), PNMT's URL and key.</li>
        <li>Create the agent's keys and join with the token.</li>
        <li>
          Install to <code>/usr/local/sbin/pnmt-agent</code>, write the systemd unit and
          <code>/etc/pnmt-agent/config.yaml</code>'s template with everything off (a file there is never touched), and
          start the service.
        </li>
        <li>
          Wait until it is connected (at most 60 s) and print both fingerprints: compare them with the agent log here.
          Exit code 0 means connected.
        </li>
      </ul>
      <p class="small muted">
        PNMT may remove keys or run scripts only where root allows it in config.yaml; the server's Agent tab shows its
        rights.
      </p>
    </template>
    <template v-else>
      <h3>On the server</h3>
      <ol class="steps">
        <li>
          Download the agent of PNMT {{ created.downloads.version }}:
          <ul>
            <li v-for="f in created.downloads.files" :key="f.architecture">
              <a :href="f.pnmtUrl ?? f.url" rel="noopener">{{ f.fileName }}</a>
              <template v-if="f.pnmtUrl"> · <a :href="f.url" rel="noopener" class="small">GitHub</a></template>
              <div v-if="f.sha256" class="mono small muted">SHA-256 {{ f.sha256 }}</div>
            </li>
          </ul>
          <div v-if="created.downloads.note" class="small muted">{{ created.downloads.note }}</div>
        </li>
        <li>
          Check it in PowerShell: <code>Get-FileHash .\pnmt-sensor-x64.exe</code>. It is not code-signed: SmartScreen
          asks, choose <em>More info → Run anyway</em>.
        </li>
        <li>Double-click it as an administrator, enter the URL and the token, and choose <em>Join</em>.</li>
        <li>Compare the fingerprints the window shows with the agent log here.</li>
      </ol>
      <h3>Unattended (GPO, Intune, RMM)</h3>
      <CopyField :value="created.commandLine" label="Command line" />
    </template>
  </div>
</template>

<style scoped>
.created {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.token {
  font-size: 2rem;
  letter-spacing: 0.15em;
  text-align: center;
  padding: var(--pnmt-space-2);
  background: var(--pnmt-bg);
  border-radius: var(--p-border-radius-md);
}

h3 {
  margin-top: var(--pnmt-space-2);
}

.steps {
  margin: 0;
  padding-left: var(--pnmt-space-4);
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.steps ul {
  margin: var(--pnmt-space-1) 0;
  padding-left: var(--pnmt-space-4);
}

.small {
  font-size: 0.857rem;
}

p {
  margin: 0;
}
</style>
