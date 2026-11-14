<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Tag from 'primevue/tag'
import { api, keys } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { keyType } from '@/views/admin/sshkeys/sshKeyFormat'
import { shortFingerprint } from '@/views/sensors/linux/linuxFormat'

/**
 * My SSH keys (docs/linux-sensor-plan.md §6.4), for every user: the keys an Admin registered as theirs, where each is authorized
 * (server and account) and when it was last used. Read-only: Admins register, retire and remove keys.
 */
const mine = useQuery({ queryKey: [...keys.sshKeys, 'mine'], queryFn: api.sshKeys.mine })
</script>

<template>
  <section class="panel" aria-labelledby="ssh-keys-title">
    <div id="ssh-keys-title" class="panel-header">My SSH keys</div>
    <div class="panel-body">
      <p v-if="mine.isPending.value" class="muted">Loading…</p>
      <p v-else-if="!mine.data.value?.length" class="muted">
        None registered for you. An Admin registers keys on Admin → SSH keys.
      </p>
      <ul v-else class="plain">
        <li v-for="k in mine.data.value" :key="k.id">
          <div class="line">
            <strong>{{ k.name }}</strong>
            <span class="muted small">{{ keyType(k.type, k.bits) }}</span>
            <span class="mono small muted" :title="k.fingerprint">{{ shortFingerprint(k.fingerprint) }}</span>
            <Tag v-if="k.state === 'Retired'" value="retired" severity="secondary" />
          </div>
          <div class="small muted">
            <template v-if="k.lastUsedAt">last used <RelativeTime :value="k.lastUsedAt" /></template>
            <template v-else>not seen in use</template>
          </div>
          <ul class="places small">
            <li v-for="p in k.where" :key="`${p.server}/${p.account}`">
              {{ p.server }} · {{ p.account }}
              <span v-if="p.lastUsedAt" class="muted"> · used <RelativeTime :value="p.lastUsedAt" /></span>
            </li>
            <li v-if="!k.where.length" class="muted">not authorized on any server</li>
          </ul>
        </li>
      </ul>
    </div>
  </section>
</template>

<style scoped>
.plain {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.line {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
  align-items: center;
}

.places {
  list-style: none;
  margin: var(--pnmt-space-1) 0 0;
  padding-left: var(--pnmt-space-3);
}

.small {
  font-size: 0.857rem;
}
</style>
