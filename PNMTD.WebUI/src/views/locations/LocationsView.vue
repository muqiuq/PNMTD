<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import TreeTable from 'primevue/treetable'
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import FilterBar from '@/components/FilterBar.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import { useMedia } from '@/composables/useMedia'
import { useSession } from '@/stores/session'
import FromTextsDialog from './FromTextsDialog.vue'
import LocationDialog from './LocationDialog.vue'
import { filledText, toTree, type LocationItem } from './locationTree'

/**
 * The locations (docs/locations-knowledge-plan.md §3.6): a tree, expanded one level, each row with its state, name and
 * description, hosts with sub-locations' (failing / all), fields filled with inherited ones counted, and when it was
 * updated. The search (names, descriptions, Text and Markdown values) shows the matches flat with their paths; the order
 * applies within each level. Users create locations, also from the hosts' texts.
 */
const session = useSession()
const route = useRoute()
const router = useRouter()
const phone = useMedia('(max-width: 599px)')

const search = ref(typeof route.query.q === 'string' ? route.query.q : '')
const debounced = ref(search.value)
let timer: ReturnType<typeof setTimeout> | undefined
watch(search, (value) => {
  clearTimeout(timer)
  timer = setTimeout(() => (debounced.value = value.trim()), 250)
})
const sort = ref(typeof route.query.sort === 'string' ? route.query.sort : 'name')
const sorts = [
  { label: 'By name', value: 'name' },
  { label: 'Failing first', value: 'state' },
  { label: 'Recently updated', value: '-updated' },
]
watch([debounced, sort], ([q, s]) =>
  router.replace({ query: { ...route.query, q: q || undefined, sort: s === 'name' ? undefined : s } }),
)

const query = useQuery({
  queryKey: computed(() => [...keys.locations, 'list', { search: debounced.value, sort: sort.value }]),
  queryFn: () => api.locations.list({ search: debounced.value || undefined, sort: sort.value }),
  placeholderData: keepPreviousData,
  refetchInterval: 30_000,
})
const items = computed(() => query.data.value?.items ?? [])
const fieldCount = computed(() => query.data.value?.fields ?? 0)
const nodes = computed(() => toTree(items.value))
const searching = computed(() => debounced.value !== '')

// One level open by default; what the person opens or closes stays so.
const expanded = ref<Record<string, boolean>>({})
let expandedOnce = false
watch(
  nodes,
  (list) => {
    if (!expandedOnce && list.length) {
      expandedOnce = true
      expanded.value = Object.fromEntries(list.filter((n) => n.children.length).map((n) => [n.key, true]))
    }
  },
  { immediate: true },
)

const creating = ref(false)
const fromTexts = ref(false)

const hostsTitle = (i: LocationItem) =>
  `${i.hosts.failing} failing of ${i.hosts.total}, sub-locations included; ${i.hosts.own} at this location itself`
const fieldsTitle = (i: LocationItem) =>
  i.inheritedFields.length ? `Inherited: ${i.inheritedFields.join(', ')}` : 'None inherited'
const open = (i: LocationItem) => router.push({ name: 'location', params: { id: i.id } })
</script>

<template>
  <div class="page">
    <PageHeader title="Locations">
      <template #status>
        <span class="muted">{{ items.length }}</span>
      </template>
      <template #actions>
        <template v-if="session.can('User')">
          <Button
            label="From the hosts' texts…"
            icon="pi pi-sparkles"
            size="small"
            severity="secondary"
            outlined
            @click="fromTexts = true"
          />
          <Button label="New location" icon="pi pi-plus" size="small" @click="creating = true" />
        </template>
      </template>
    </PageHeader>
    <FilterBar v-model:search="search" placeholder="Search names, descriptions and text fields">
      <Select
        v-model="sort"
        :options="sorts"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Order"
      />
    </FilterBar>

    <ErrorState
      v-if="query.isError.value && !query.data.value"
      :error="query.error.value"
      what="the locations"
      @retry="query.refetch()"
    />
    <div v-else-if="!query.data.value" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
    <EmptyState
      v-else-if="!items.length && !searching"
      icon="pi pi-map-marker"
      title="No locations yet."
      text="A location is a site, a building or a room: its hosts, its information, its pages."
      class="panel"
    >
      <Button v-if="session.can('User')" label="From the hosts' texts…" size="small" text @click="fromTexts = true" />
    </EmptyState>
    <DataTable
      v-else-if="searching"
      :value="items"
      size="small"
      data-key="id"
      class="panel"
      row-hover
      @row-click="(e: { data: LocationItem }) => open(e.data)"
    >
      <Column header="" style="width: 2.5rem">
        <template #body="{ data }"><StatusBadge :status="data.state" icon-only /></template>
      </Column>
      <Column header="Location">
        <template #body="{ data }">
          <RouterLink :to="{ name: 'location', params: { id: data.id } }" class="strong">{{ data.path }}</RouterLink>
          <div v-if="data.description" class="muted small">{{ data.description }}</div>
        </template>
      </Column>
      <Column v-if="!phone" header="Hosts" style="width: 7rem">
        <template #body="{ data }"
          ><span :title="hostsTitle(data)">{{ data.hosts.failing }} / {{ data.hosts.total }}</span></template
        >
      </Column>
      <template #empty><EmptyState title="No location matches." /></template>
    </DataTable>
    <TreeTable v-else v-model:expanded-keys="expanded" :value="nodes" size="small" class="panel tree" :row-hover="true">
      <Column field="name" header="Location" expander>
        <template #body="{ node }">
          <span class="name-cell">
            <StatusBadge :status="node.data.state" icon-only />
            <span class="names">
              <RouterLink :to="{ name: 'location', params: { id: node.data.id } }" class="strong">{{
                node.data.name
              }}</RouterLink>
              <span v-if="node.data.description" class="muted small description">{{ node.data.description }}</span>
            </span>
          </span>
        </template>
      </Column>
      <Column header="Hosts" style="width: 7rem">
        <template #body="{ node }">
          <span class="counts" :title="hostsTitle(node.data)">
            <span :class="{ fail: node.data.hosts.failing }">{{ node.data.hosts.failing }}</span> /
            {{ node.data.hosts.total }}
          </span>
        </template>
      </Column>
      <Column v-if="!phone" header="Fields" style="width: 7rem">
        <template #body="{ node }">
          <span :title="fieldsTitle(node.data)">{{ filledText(node.data, fieldCount) }}</span>
          <i
            v-if="node.data.inheritedFields.length"
            class="pi pi-arrow-down-left muted inherited"
            :title="fieldsTitle(node.data)"
            aria-hidden="true"
          />
        </template>
      </Column>
      <Column v-if="!phone" header="Updated" style="width: 9rem">
        <template #body="{ node }">
          <RelativeTime :value="node.data.updatedAt" />
        </template>
      </Column>
    </TreeTable>

    <LocationDialog
      v-model:visible="creating"
      @saved="(d) => router.push({ name: 'location', params: { id: d.id } })"
    />
    <FromTextsDialog v-model:visible="fromTexts" />
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.tree,
.panel {
  overflow: hidden;
}

.name-cell {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.names {
  display: inline-flex;
  align-items: baseline;
  gap: var(--pnmt-space-2);
  min-width: 0;
  flex-wrap: wrap;
}

.strong {
  font-weight: 500;
}

.small {
  font-size: 0.857rem;
}

.counts {
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.fail {
  color: var(--pnmt-fail);
  font-weight: 600;
}

.inherited {
  margin-left: var(--pnmt-space-1);
  font-size: 0.75rem;
}

:deep(.p-datatable-tbody > tr) {
  cursor: pointer;
}
</style>
