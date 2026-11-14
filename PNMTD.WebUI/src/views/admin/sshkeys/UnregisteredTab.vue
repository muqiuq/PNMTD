<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { useRoute } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import { shortFingerprint } from '@/views/sensors/linux/linuxFormat'
import AddKeysDialog from './AddKeysDialog.vue'
import RemoveKeysDialog from './RemoveKeysDialog.vue'
import { keyType, noteLabel, targetsOf, type RemovalSource } from './sshKeyFormat'
import { useQueryParam } from './useQueryParam'

/**
 * Keys found on servers that aren't registered (docs/linux-sensor-plan.md §11.4), one row per fingerprint: the comments seen,
 * type and bits, where (servers and accounts), first seen, last use, notes. The inventory's work list: Register… (one, or several
 * to one owner, prefilled from the comments) or Remove… from wherever they are.
 */
const route = useRoute()
const filter = useQueryParam<'weak' | 'unused180'>('filter')
const sensorId = computed(() => (typeof route.query.sensorId === 'string' ? route.query.sensorId : undefined))
const filters = computed(() => ({ filter: filter.value ?? undefined, sensorId: sensorId.value }))
type Row = S['SshKeyUnregisteredItem'] & { id: string }
const table = useServerTable<Row>({
  key: [...keys.sshKeys, 'unregistered'],
  fetch: async (q) => {
    const page = await api.sshKeys.unregistered(q)
    return { ...page, items: page.items.map((i) => ({ ...i, id: i.fingerprint })) }
  },
  defaultSort: '-firstseen',
  pageSize: 50,
  filters,
  syncUrl: true,
})
const choices = [
  { label: 'Weak', value: 'weak' },
  { label: 'Unused for 180 days', value: 'unused180' },
]

// The selection survives paging: the rows by fingerprint.
const selected = ref(new Map<string, Row>())
const toggle = (r: Row, on: boolean) => {
  const next = new Map(selected.value)
  if (on) next.set(r.id, r)
  else next.delete(r.id)
  selected.value = next
}
const chosen = computed(() => [...selected.value.values()])
const registering = ref<string | null>(null)
const removing = ref<RemovalSource | null>(null)
const register = (rows: Row[]) =>
  (registering.value = rows
    .map((r) => r.publicKey)
    .filter(Boolean)
    .join('\n'))
const remove = (rows: Row[]) =>
  (removing.value = {
    kind: 'items',
    items: targetsOf(rows.flatMap((r) => r.where)),
    reason: 'Unregistered',
    title: rows.length === 1 ? `Remove ${shortFingerprint(rows[0]!.fingerprint)}` : `Remove ${rows.length} keys`,
  })
const registerOpen = computed({ get: () => registering.value !== null, set: (v) => !v && (registering.value = null) })
const menu = (r: Row) => [
  { label: 'Register…', icon: 'pi pi-key', disabled: !r.publicKey, command: () => register([r]) },
  { label: 'Remove…', icon: 'pi pi-trash', command: () => remove([r]) },
]
const done = () => (selected.value = new Map())
</script>

<template>
  <div class="tab">
    <FilterBar v-model:search="table.search.value" placeholder="Fingerprint, comment, or a public key">
      <Select
        v-model="filter"
        :options="choices"
        option-label="label"
        option-value="value"
        show-clear
        size="small"
        placeholder="Every key"
        aria-label="Filter"
      />
      <template #end>
        <span v-if="chosen.length" class="muted small">{{ chosen.length }} chosen</span>
        <Button label="Register…" icon="pi pi-key" size="small" :disabled="!chosen.length" @click="register(chosen)" />
        <Button
          label="Remove…"
          icon="pi pi-trash"
          size="small"
          severity="danger"
          outlined
          :disabled="!chosen.length"
          @click="remove(chosen)"
        />
      </template>
    </FilterBar>
    <ListTable :table="table" what="keys" empty-text="Every key found on the servers is registered.">
      <Column style="width: 3rem">
        <template #body="{ data }">
          <Checkbox
            :model-value="selected.has(data.id)"
            binary
            :aria-label="`Choose ${data.fingerprint}`"
            @update:model-value="(v: boolean) => toggle(data, v)"
          />
        </template>
      </Column>
      <Column header="Key">
        <template #body="{ data }">
          <strong>{{ data.comments.join(', ') || 'no comment' }}</strong>
          <div class="small muted">
            {{ keyType(data.type, data.bits) }} ·
            <span class="mono" :title="data.fingerprint">{{ shortFingerprint(data.fingerprint) }}</span>
          </div>
        </template>
      </Column>
      <Column header="Where" field="servers" sortable style="width: 18rem">
        <template #body="{ data }">
          <div v-for="w in data.where.slice(0, 3)" :key="w.id" class="small">
            <RouterLink :to="{ name: 'sensor', params: { id: w.server.id, tab: 'ssh-keys' } }">{{
              w.server.name
            }}</RouterLink>
            · {{ w.account }}
          </div>
          <div v-if="data.where.length > 3" class="small muted">and {{ data.where.length - 3 }} more</div>
        </template>
      </Column>
      <Column header="First seen" field="firstseen" sortable style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.firstSeenAt" /></template>
      </Column>
      <Column header="Last used" field="lastused" sortable style="width: 11rem">
        <template #body="{ data }">
          <template v-if="data.lastUse">
            <RelativeTime :value="data.lastUse.at" />
            <div class="muted small">{{ data.lastUse.server.name }} · {{ data.lastUse.account }}</div>
          </template>
          <span v-else class="muted small">not seen</span>
        </template>
      </Column>
      <Column header="Notes" style="width: 12rem">
        <template #body="{ data }">
          <div class="tags">
            <Tag v-if="data.weakness" :value="data.weakness" severity="danger" class="small" />
            <Tag
              v-for="n in data.notes.filter((n: string) => n !== 'weak' && n !== data.weakness)"
              :key="n"
              :value="noteLabel(n)"
              severity="secondary"
              class="small"
            />
          </div>
        </template>
      </Column>
      <Column style="width: 3.5rem">
        <template #body="{ data }"
          ><MoreMenu :items="menu(data)" :label="`Actions for ${data.fingerprint}`"
        /></template>
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <strong>{{ item.comments.join(', ') || shortFingerprint(item.fingerprint) }}</strong>
          <Tag v-if="item.weakness" value="weak" severity="danger" />
        </div>
        <div class="small muted">{{ item.servers }} servers · {{ item.accounts }} accounts</div>
      </template>
    </ListTable>
    <AddKeysDialog
      v-model:visible="registerOpen"
      :prefill="registering"
      title="Register keys found on servers"
      @registered="done"
    />
    <RemoveKeysDialog v-model:source="removing" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.tags,
.line {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
  align-items: center;
}

.small {
  font-size: 0.857rem;
}
</style>
