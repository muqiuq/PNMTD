<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import { api, keys } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { formatAbsolute } from '@/format'

/**
 * PNMTD's SSH keys (docs/mikrotik-plan.md §4.2): Rotate… creates a new pair for join scripts, and PNMTD offers
 * the older keys until they are retired, so every router keeps working until its new script is pasted. The
 * routers that may still need an older key are listed; retiring them is refused until they have the new one.
 */
const q = useQuery({ queryKey: [...keys.mikrotik, 'ssh-keys'], queryFn: api.mikrotik.sshKeys, refetchInterval: 60_000 })
const { done, ask } = useActions()

async function rotate() {
  if (
    await ask(
      'Rotate PNMTD’s SSH key?',
      'A new key pair goes into every join script from now on. The current key is still offered, so routers keep working; each needs its new join script pasted, and then the old key can be retired.',
      'Rotate',
    )
  ) {
    await done(() => api.mikrotik.rotateSshKey(), { success: 'New SSH key created', invalidate: [keys.mikrotik] })
  }
}

async function retire() {
  const waiting = q.data.value?.waiting ?? []
  const forced = waiting.length > 0
  if (
    await ask(
      'Retire the older keys?',
      forced
        ? `${waiting.length} router(s) may still accept only an older key and fail with 441 until their new join script is pasted: ${waiting.map((w) => w.name).join(', ')}.`
        : 'PNMTD stops offering them. Every router has the current key.',
      forced ? 'Retire anyway' : 'Retire',
    )
  ) {
    await done(() => api.mikrotik.retireSshKeys(forced), { success: 'Older keys retired', invalidate: [keys.mikrotik] })
  }
}
</script>

<template>
  <div v-if="q.data.value" class="keys">
    <div v-for="(k, i) in q.data.value.keys" :key="k.fingerprint" class="key" :class="{ retired: k.retiredAt }">
      <span class="mono">{{ k.fingerprint }}</span>
      <Tag v-if="i === 0" value="current" severity="success" />
      <Tag v-else-if="!k.retiredAt" value="still offered" severity="warn" />
      <span class="muted small">
        {{ k.comment }} · {{ formatAbsolute(k.createdAt) }}
        <template v-if="k.retiredAt"> · retired {{ formatAbsolute(k.retiredAt) }}</template>
      </span>
    </div>
    <div v-if="q.data.value.waiting.length" class="small waiting">
      <strong>{{ q.data.value.waiting.length }} router(s) may still need an older key:</strong>
      <span v-for="(w, i) in q.data.value.waiting" :key="w.sensorId">
        <RouterLink :to="{ name: 'sensor', params: { id: w.sensorId, tab: 'setup' } }" :title="w.reason">{{
          w.name
        }}</RouterLink><span v-if="i < q.data.value.waiting.length - 1">, </span>
      </span>
    </div>
    <div class="actions">
      <Button label="Rotate…" icon="pi pi-refresh" size="small" severity="secondary" @click="rotate" />
      <Button
        v-if="q.data.value.keys.filter((k) => !k.retiredAt).length > 1"
        label="Retire older keys…"
        size="small"
        text
        severity="danger"
        @click="retire"
      />
    </div>
  </div>
</template>

<style scoped>
.keys {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.key {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.retired {
  opacity: 0.6;
}

.mono {
  font-family: var(--pnmt-mono);
  overflow-wrap: anywhere;
}

.small {
  font-size: 0.857rem;
}

.waiting {
  color: var(--pnmt-warn);
}

.actions {
  display: flex;
  gap: var(--pnmt-space-2);
}
</style>
