<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import type { S } from '@/api/resources'
import CopyField from './CopyField.vue'
import QrCode from './QrCode.vue'

/**
 * A custom tunnel's configuration (docs/mikrotik-plan.md §3.5): the wg-quick file to download or copy, and
 * the same as a QR code for the WireGuard app on a phone. It holds a private key; the download was
 * audit-logged when it was fetched.
 */
const visible = defineModel<boolean>('visible', { required: true })
defineProps<{ config: S['WireGuardTunnelConfig'] | null; name: string }>()

function download(c: S['WireGuardTunnelConfig']) {
  const url = URL.createObjectURL(new Blob([c.content], { type: 'text/plain' }))
  const a = document.createElement('a')
  a.href = url
  a.download = c.fileName
  a.click()
  URL.revokeObjectURL(url)
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="`Tunnel ${name}`"
    modal
    :style="{ width: '46rem' }"
    :breakpoints="{ '760px': '95vw' }"
  >
    <div v-if="config" class="body">
      <Message severity="warn" :closable="false" size="small">
        It contains a private key: keep it like a password. Import the file into the WireGuard app (Windows, macOS,
        Linux), or scan the code with the app on a phone.
      </Message>
      <div class="columns">
        <pre class="conf" aria-label="Tunnel configuration">{{ config.content }}</pre>
        <QrCode :text="config.content" :size="220" :label="`QR code of the tunnel ${name}`" />
      </div>
      <p class="muted small">
        Through it, WinBox, SSH and WebFig reach the routers you may reach, on the ports
        {{ config.managementPorts.join(', ') }}.
      </p>
    </div>
    <template #footer>
      <CopyField v-if="config" :value="config.content" label="Tunnel configuration" display="Copy" />
      <Button v-if="config" :label="`Download ${config.fileName}`" icon="pi pi-download" @click="download(config)" />
    </template>
  </Dialog>
</template>

<style scoped>
.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.columns {
  display: flex;
  gap: var(--pnmt-space-3);
  align-items: flex-start;
  flex-wrap: wrap;
}

.conf {
  flex: 1;
  min-width: 18rem;
  margin: 0;
  padding: var(--pnmt-space-2);
  font-family: var(--pnmt-mono);
  font-size: 0.8rem;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  white-space: pre;
  overflow: auto;
}

.small {
  font-size: 0.857rem;
  margin: 0;
}
</style>
