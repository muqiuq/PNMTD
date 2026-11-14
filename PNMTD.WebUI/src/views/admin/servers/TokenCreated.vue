<script setup lang="ts">
import Message from 'primevue/message'
import type { S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'

/**
 * A new join token (docs/windows-sensor-plan.md §3.3; docs/linux-sensor-plan.md §11.1), shown once: the token in large type,
 * PNMT's URL and key, and for Windows the downloads with their SHA-256, the command line for unattended installs and what to
 * do on the server. Linux's install line comes with the Linux agent's release (L3).
 */
defineProps<{ created: S['AgentJoinTokenCreated'] }>()
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
    <template v-if="created.item.platform === 'Linux'">
      <h3>On the server</h3>
      <Message severity="info" :closable="false" size="small">
        The Linux agent's install line comes with its release: until then, keep the token for it, or revoke it on
        <em>Join tokens</em>. A Windows agent can't use it.
      </Message>
    </template>
    <template v-else>
      <h3>On the server</h3>
      <ol class="steps">
        <li>
          Download the agent of PNMT {{ created.downloads.version }}:
          <ul>
            <li v-for="f in created.downloads.files" :key="f.architecture">
              <a :href="f.url" rel="noopener">{{ f.fileName }}</a>
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
</style>
