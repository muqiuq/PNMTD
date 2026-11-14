<script setup lang="ts">
import Column from 'primevue/column'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import { shortFingerprint } from '@/views/sensors/linux/linuxFormat'
import EditKeyDialog from './EditKeyDialog.vue'
import RemoveKeysDialog from './RemoveKeysDialog.vue'
import { keyFilters, keyType, noteLabel, noteSeverity } from './sshKeyFormat'
import { useKeyActions } from './useKeyActions'
import { useQueryParam } from './useQueryParam'

/**
 * The register (docs/linux-sensor-plan.md §11.4): name, owner (a user, or the text, and former user), kind, type and bits,
 * fingerprint, where it is authorized (servers and accounts, counted), last use, state and notes (weak, unused for 180 days, owner
 * deactivated, retired but still authorized). Filters by owner, kind, state and note (from the strip too: ?filter=); the search
 * takes a name, fingerprint, comment, owner or a pasted public key. A row opens the key's drawer; its menu has Edit…, Retire… or
 * Reactivate, Remove from servers…, Delete….
 */
const emit = defineEmits<{ open: [id: string] }>()
const route = useRoute()
// The strip's figures open these through the URL (?filter=weak, ?state=Retired).
const filter = useQueryParam<string>('filter')
const state = useQueryParam<S['SshKeyState']>('state')
const kind = ref<S['SshKeyKind'] | null>(null)
const owner = ref(typeof route.query.owner === 'string' ? route.query.owner : '')
const ownerFilter = ref(owner.value)
let timer: ReturnType<typeof setTimeout> | undefined
watch(owner, (v) => {
  clearTimeout(timer)
  timer = setTimeout(() => (ownerFilter.value = v.trim()), 300)
})
const filters = computed(() => ({
  filter: filter.value ?? undefined,
  state: state.value ?? undefined,
  kind: kind.value ?? undefined,
  owner: ownerFilter.value || undefined,
}))
type Row = S['SshKeyItem']
const table = useServerTable<Row>({
  key: [...keys.sshKeys, 'keys'],
  fetch: (q) => api.sshKeys.list(q),
  defaultSort: 'name',
  pageSize: 50,
  filters,
  syncUrl: true,
})
const states = [
  { label: 'Active', value: 'Active' },
  { label: 'Retired', value: 'Retired' },
]
const kinds = [
  { label: 'Person', value: 'Person' },
  { label: 'Service', value: 'Service' },
]
const actions = useKeyActions()
const { editing, removing } = actions
</script>

<template>
  <div class="tab">
    <FilterBar
      v-model:search="table.search.value"
      placeholder="Name, fingerprint, comment, or a public key"
      :active="[filter, state, kind, ownerFilter].filter(Boolean).length"
    >
      <InputText v-model="owner" size="small" placeholder="Owner" aria-label="Owner" class="owner" />
      <Select
        v-model="kind"
        :options="kinds"
        option-label="label"
        option-value="value"
        show-clear
        size="small"
        placeholder="Every kind"
        aria-label="Kind"
      />
      <Select
        v-model="state"
        :options="states"
        option-label="label"
        option-value="value"
        show-clear
        size="small"
        placeholder="Every state"
        aria-label="State"
      />
      <Select
        v-model="filter"
        :options="keyFilters"
        option-label="label"
        option-value="value"
        show-clear
        size="small"
        placeholder="Every key"
        aria-label="Note"
      />
    </FilterBar>
    <ListTable
      :table="table"
      what="keys"
      selectable
      empty-text="No key matches. Add keys… registers keys by pasting them."
      :row-class="(k) => (k.state === 'Retired' ? 'row-muted' : undefined)"
      @select="(k) => emit('open', k.id)"
    >
      <Column header="Name" field="name" sortable>
        <template #body="{ data }">
          <strong>{{ data.name }}</strong>
          <div v-if="data.note" class="muted small ellipsis">{{ data.note }}</div>
        </template>
      </Column>
      <Column header="Owner" field="owner" sortable style="width: 13rem">
        <template #body="{ data }">
          {{ data.owner.name }}
          <div class="muted small">{{ data.kind === 'Person' ? 'person' : 'service' }}</div>
        </template>
      </Column>
      <Column header="Key" style="width: 14rem">
        <template #body="{ data }">
          {{ keyType(data.type, data.bits) }}
          <div class="mono small muted" :title="data.fingerprint">{{ shortFingerprint(data.fingerprint) }}</div>
        </template>
      </Column>
      <Column header="Authorized" field="servers" sortable style="width: 8rem">
        <template #body="{ data }">
          <template v-if="data.servers">
            {{ data.servers }} {{ data.servers === 1 ? 'server' : 'servers' }}
            <div class="muted small">{{ data.accounts }} {{ data.accounts === 1 ? 'account' : 'accounts' }}</div>
          </template>
          <span v-else class="muted">nowhere</span>
        </template>
      </Column>
      <Column header="Last used" field="lastused" sortable style="width: 11rem">
        <template #body="{ data }">
          <template v-if="data.lastUse">
            <RelativeTime :value="data.lastUse.at" />
            <div class="muted small">
              {{ data.lastUse.server.name }} · {{ data.lastUse.account
              }}<template v-if="data.lastUse.from">
                from <span class="mono">{{ data.lastUse.from }}</span></template
              >
            </div>
          </template>
          <span v-else class="muted small">not seen</span>
        </template>
      </Column>
      <Column header="State" field="state" sortable style="width: 13rem">
        <template #body="{ data }">
          <div class="tags">
            <Tag v-if="data.state === 'Retired'" value="retired" severity="secondary" />
            <Tag v-for="n in data.notes" :key="n" :value="noteLabel(n)" :severity="noteSeverity(n)" class="small" />
          </div>
        </template>
      </Column>
      <Column style="width: 3.5rem">
        <template #body="{ data }">
          <span @click.stop><MoreMenu :items="actions.menu(data)" :label="`Actions for ${data.name}`" /></span>
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <strong>{{ item.name }}</strong>
          <Tag v-if="item.state === 'Retired'" value="retired" severity="secondary" />
        </div>
        <div class="small muted">
          {{ item.owner.name }} · {{ keyType(item.type, item.bits) }} · {{ item.servers }} servers
        </div>
      </template>
    </ListTable>
    <EditKeyDialog v-model:item="editing" />
    <RemoveKeysDialog v-model:source="removing" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.owner {
  width: 10rem;
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
