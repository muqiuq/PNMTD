<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Message from 'primevue/message'
import { computed, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { api, keys } from '@/api/resources'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import AddKeysDialog from './AddKeysDialog.vue'
import KeyDrawer from './KeyDrawer.vue'
import KeysTab from './KeysTab.vue'
import RemovalsTab from './RemovalsTab.vue'
import RemoveKeysDialog from './RemoveKeysDialog.vue'
import type { RemovalSource } from './sshKeyFormat'
import UnregisteredTab from './UnregisteredTab.vue'

/**
 * Admin → SSH keys, the key manager (docs/linux-sensor-plan.md §6.4, §11.4): a strip of figures that open their filter
 * (registered, unregistered, retired keys still authorized, deactivated users' keys still authorized with Remove all…, weak,
 * unused for 180 days, pending removals), then the register, the keys found unregistered on servers, and every removal. A key
 * opens its drawer (?key= links one). PNMT names and assigns keys; it never deploys them. Admin.
 */
const route = useRoute()
const tab = computed(() => (typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'keys'))
const to = (t: string, query?: Record<string, string>) => ({ name: 'ssh-keys', params: { tab: t }, query })
const tabs = computed(() => [
  { key: 'keys', label: 'Keys', icon: 'pi pi-key', to: to('keys') },
  {
    key: 'unregistered',
    label: 'Unregistered',
    icon: 'pi pi-question-circle',
    to: to('unregistered'),
    badge: summary.data.value?.unregistered || null,
  },
  { key: 'removals', label: 'Removals', icon: 'pi pi-trash', to: to('removals') },
])
const summary = useQuery({
  queryKey: [...keys.sshKeys, 'summary'],
  queryFn: api.sshKeys.summary,
  refetchInterval: 30_000,
})
const s = computed(() => summary.data.value ?? null)
const figures = computed(() => {
  const v = s.value
  if (!v) return []
  return [
    { label: 'Registered', value: v.registered, sub: `${v.active} active · ${v.retired} retired`, to: to('keys') },
    {
      label: 'Unregistered',
      value: v.unregistered,
      sub: 'found on servers',
      to: to('unregistered'),
      warn: v.unregistered > 0,
    },
    {
      label: 'Retired, still authorized',
      value: v.retiredStillAuthorized,
      sub: 'to remove',
      to: to('keys', { filter: 'retiredStillAuthorized' }),
      fail: v.retiredStillAuthorized > 0,
    },
    {
      label: "Deactivated users' keys",
      value: v.deactivatedUsersKeysAuthorized,
      sub: 'still authorized',
      to: to('keys', { filter: 'ownerDeactivated' }),
      fail: v.deactivatedUsersKeysAuthorized > 0,
    },
    { label: 'Weak', value: v.weak, sub: 'registered or found', to: to('keys', { filter: 'weak' }), warn: v.weak > 0 },
    { label: 'Unused', value: v.unused180, sub: 'for 180 days', to: to('keys', { filter: 'unused180' }) },
    {
      label: 'Pending removals',
      value: v.pendingRemovals,
      sub: 'waiting for a server',
      to: to('removals', { state: 'Pending' }),
    },
  ]
})

const drawerKey = ref<string | null>(typeof route.query.key === 'string' ? route.query.key : null)
watch(
  () => route.query.key,
  (k) => typeof k === 'string' && (drawerKey.value = k),
)
const adding = ref(false)
const removing = ref<RemovalSource | null>(null)
</script>

<template>
  <div class="page">
    <PageHeader title="SSH keys">
      <template #actions>
        <Button label="Add keys…" icon="pi pi-plus" size="small" @click="adding = true" />
      </template>
    </PageHeader>
    <nav v-if="s" class="strip panel" aria-label="Key figures">
      <RouterLink
        v-for="f in figures"
        :key="f.label"
        :to="f.to"
        class="cell"
        :class="{ warn: f.warn, fail: f.fail }"
        :aria-label="`${f.label}: ${f.value}`"
      >
        <span class="label">{{ f.label }}</span>
        <span class="value">{{ f.value }}</span>
        <span class="muted small">{{ f.sub }}</span>
      </RouterLink>
    </nav>
    <Message v-for="d in s?.deactivatedUsers ?? []" :key="d.user.id" severity="warn" :closable="false">
      <div class="offboard">
        <span
          ><strong>{{ d.user.displayName || d.user.username }}</strong> ({{ d.user.username }}) is deactivated, and
          {{ d.keys === 1 ? 'a key of theirs is' : `${d.keys} of their keys are` }} still authorized in
          {{ d.authorizations }} {{ d.authorizations === 1 ? 'place' : 'places' }}.</span
        >
        <Button
          label="Remove all…"
          icon="pi pi-trash"
          size="small"
          severity="danger"
          outlined
          @click="removing = { kind: 'user', userId: d.user.id, name: d.user.username }"
        />
      </div>
    </Message>
    <TabNav :items="tabs" :active="tab" label="SSH keys" />
    <UnregisteredTab v-if="tab === 'unregistered'" />
    <RemovalsTab v-else-if="tab === 'removals'" @open="(id) => (drawerKey = id)" />
    <KeysTab v-else @open="(id) => (drawerKey = id)" />
    <KeyDrawer v-model:key-id="drawerKey" />
    <AddKeysDialog v-model:visible="adding" />
    <RemoveKeysDialog v-model:source="removing" />
  </div>
</template>

<style scoped>
.strip {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr));
  gap: var(--pnmt-space-1);
  padding: var(--pnmt-space-2);
}

.cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-radius: var(--p-border-radius-md);
  color: inherit;
  text-decoration: none;
  border: 1px solid transparent;
}

.cell:hover,
.cell:focus-visible {
  background: var(--pnmt-bg);
  border-color: var(--pnmt-border);
}

.cell.warn {
  background: var(--pnmt-warn-soft);
}

.cell.fail {
  background: var(--pnmt-fail-soft);
}

.label {
  font-size: 0.786rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pnmt-muted);
}

.value {
  font-size: 1.4rem;
  font-weight: 600;
  line-height: 1.2;
}

.offboard {
  display: flex;
  gap: var(--pnmt-space-3);
  align-items: center;
  flex-wrap: wrap;
}

.small {
  font-size: 0.857rem;
}
</style>
