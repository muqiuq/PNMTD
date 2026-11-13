<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import { ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import TunnelConfigDialog from '@/components/TunnelConfigDialog.vue'
import { useActions } from '@/composables/useActions'

/**
 * A user's own WireGuard tunnels (docs/mikrotik-plan.md §3.5, §11.3): the ones an Admin made for them, each
 * downloadable as a file or a QR code while it is enabled and not expired.
 */
const { run } = useActions()
const tunnels = useQuery({ queryKey: [...keys.mikrotik, 'my-tunnels'], queryFn: api.wireGuard.myTunnels })
const config = ref<S['WireGuardTunnelConfig'] | null>(null)
const name = ref('')
const open = ref(false)

async function download(t: S['WireGuardTunnelItem']) {
  const c = await run(() => api.wireGuard.myTunnelConfig(t.id), { failure: 'No configuration' })
  if (c) {
    config.value = c
    name.value = t.name
    open.value = true
  }
}
</script>

<template>
  <div class="page">
    <PageHeader title="Tunnels" />
    <EmptyState
      v-if="tunnels.data.value && !tunnels.data.value.length"
      icon="pi pi-lock"
      title="No tunnel for you."
      text="An Admin can make one under Admin → MikroTik & WireGuard → Custom tunnels."
    />
    <ul v-else class="list">
      <li v-for="t in tunnels.data.value ?? []" :key="t.id" class="panel panel-body item">
        <div class="grow">
          <strong>{{ t.name }}</strong> <span class="mono muted">{{ t.address }}</span>
          <div class="muted small">
            {{ t.reaches.length }} router(s):
            {{ t.reaches.map((r) => `${r.name} (${r.address})`).join(', ') || 'none yet' }}
          </div>
          <div class="muted small">
            <template v-if="!t.enabled">Disabled.</template>
            <template v-else-if="t.expired">Expired.</template>
            <template v-else-if="t.expiresAt">Expires <RelativeTime :value="t.expiresAt" />.</template>
            <template v-if="t.latestHandshake"> Last connected <RelativeTime :value="t.latestHandshake" />.</template>
          </div>
        </div>
        <Button
          label="Configuration"
          icon="pi pi-download"
          size="small"
          :disabled="!t.enabled || t.expired"
          @click="download(t)"
        />
      </li>
    </ul>
    <TunnelConfigDialog v-model:visible="open" :config="config" :name="name" />
  </div>
</template>

<style scoped>
.list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  max-width: 760px;
}

.item {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
}

.grow {
  flex: 1;
  min-width: 0;
}

.mono {
  font-family: var(--pnmt-mono);
}

.small {
  font-size: 0.857rem;
}
</style>
