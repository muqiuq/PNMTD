<script setup lang="ts">
import Message from 'primevue/message'
import type { S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'

/**
 * A new join token (docs/windows-sensor-plan.md §3.3), shown once: the token in large type, PNMT's URL and key, the
 * downloads with their SHA-256, the command line for unattended installs, and what to do on the server.
 */
defineProps<{ created: S['WindowsJoinTokenCreated'] }>()
</script>

<template>
  <div class="created">
    <Message severity="warn" :closable="false" size="small">
      The token is shown only now: PNMT keeps its hash. It is valid once, until
      {{ new Date(created.item.expiresAt).toLocaleString() }}.
    </Message>
    <div class="token mono" aria-label="Join token">{{ created.token }}</div>
    <CopyField :value="created.token" label="Join token" />
    <dl class="facts">
      <dt>PNMT's URL</dt>
      <dd><CopyField :value="created.url" label="PNMT's URL" /></dd>
      <dt>PNMT's key</dt>
      <dd class="mono small">{{ created.pnmtdFingerprint }}</dd>
      <dt>Host</dt>
      <dd>
        {{ created.item.host.name
        }}<template v-if="created.item.rejoin"> · re-join of {{ created.item.rejoin.name }}</template>
      </dd>
    </dl>
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
