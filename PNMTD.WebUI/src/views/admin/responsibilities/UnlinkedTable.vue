<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
import { computed, ref, watch } from 'vue'
import { kbLinkRoute } from '@/api/kb'
import { responsibilitiesApi, responsibilityKeys, type ResponsibilityUnlinkedRow } from '@/api/responsibilities'
import ColourDot from '@/components/ColourDot.vue'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import { useServerTable } from '@/composables/useServerTable'
import { useSession } from '@/stores/session'
import LocationPicker from '@/views/locations/LocationPicker.vue'
import BulkLinkDialog from './BulkLinkDialog.vue'
import { linksToQuery, type LinksState } from './linksState'

/**
 * The hosts or locations no responsibility links itself (docs/responsibilities-extensions-plan.md §5.1), with the responsibilities
 * that link their location or a parent of it: nothing is inherited, so those show as a hint, and *Count a linked location as
 * covering what is in it* leaves them out of this view only. With Edit, the rows selected are linked to a responsibility at once.
 */
const props = defineProps<{ kind: 'Host' | 'Location'; edit: boolean }>()
const state = defineModel<LinksState>('state', { required: true })
const session = useSession()

const filters = computed(() => ({
  location: state.value.location ?? undefined,
  below: state.value.location && !state.value.below ? false : undefined,
  covered: state.value.covered || undefined,
}))
const table = useServerTable<ResponsibilityUnlinkedRow>({
  key: [...responsibilityKeys.all, 'unlinked', props.kind],
  fetch: (q) => responsibilitiesApi.unlinked(props.kind, q),
  defaultSort: 'object',
  pageSize: 50,
  filters,
  syncUrl: true,
  urlQuery: computed(() => linksToQuery(state.value)),
})
const active = computed(() => (state.value.location ? 1 : 0) + (state.value.covered ? 1 : 0))
const what = computed(() => (props.kind === 'Host' ? 'hosts' : 'locations'))
// The location filter lists the tree, which needs Locations: View.
const canLocations = computed(() => session.can('Locations', 'View'))

const all = useQuery({
  queryKey: responsibilityKeys.list({}),
  queryFn: () => responsibilitiesApi.list({}),
  enabled: computed(() => props.edit),
})
const options = computed(() =>
  (all.data.value?.items ?? []).map((r) => ({ value: r.id, label: r.name, color: r.color ?? null })),
)
const selection = ref<ResponsibilityUnlinkedRow[]>([])
watch(filters, () => (selection.value = []), { deep: true })
const objects = computed(() => selection.value.map((r) => ({ kind: props.kind, id: r.id, name: r.name })))
const dialogOpen = ref(false)
</script>

<template>
  <div class="tab">
    <FilterBar v-model:search="table.search.value" :placeholder="`Name, where…`" :active="active">
      <template v-if="canLocations">
        <LocationPicker v-model="state.location" none-label="All locations" size="small" aria-label="Location" />
        <label v-if="state.location" class="check">
          <Checkbox v-model="state.below" binary :input-id="`unlinked-below-${kind}`" />
          <span>and below</span>
        </label>
      </template>
      <label class="check">
        <Checkbox v-model="state.covered" binary :input-id="`unlinked-covered-${kind}`" />
        <span>Count a linked location as covering what is in it</span>
      </label>
    </FilterBar>
    <div v-if="edit && selection.length" class="selected" role="status" data-test="unlinked-selected">
      <span>{{ selection.length }} selected</span>
      <Button label="Link to…" icon="pi pi-link" size="small" data-test="link-selected" @click="dialogOpen = true" />
      <Button label="Clear" size="small" severity="secondary" text @click="selection = []" />
    </div>
    <ListTable
      v-model:selection="selection"
      :table="table"
      :what="what"
      :empty-text="`Every one of these ${what} has a responsibility.`"
    >
      <Column v-if="edit" selection-mode="multiple" style="width: 3rem" />
      <Column :header="kind === 'Host' ? 'Host' : 'Location'" field="object" sortable>
        <template #body="{ data }">
          <RouterLink :to="kbLinkRoute({ kind: data.kind, id: data.id })">{{ data.name }}</RouterLink>
        </template>
      </Column>
      <Column header="Where" field="where" sortable>
        <template #body="{ data }">
          <span class="muted">{{ data.detail }}</span>
        </template>
      </Column>
      <Column header="Through its location">
        <template #body="{ data }">
          <span v-for="r in data.throughLocation" :key="r.id" class="through">
            <ColourDot :colour="r.color" />
            <RouterLink :to="{ name: 'responsibility', params: { id: r.id } }">{{ r.name }}</RouterLink>
          </span>
        </template>
      </Column>
      <template #mobile="{ item }">
        <RouterLink :to="kbLinkRoute({ kind: item.kind, id: item.id })" class="strong">{{ item.name }}</RouterLink>
        <div v-if="item.detail" class="muted small">{{ item.detail }}</div>
        <div v-if="item.throughLocation.length" class="small">
          Through its location: {{ item.throughLocation.map((r) => r.name).join(', ') }}
        </div>
      </template>
    </ListTable>
    <BulkLinkDialog
      v-model:visible="dialogOpen"
      mode="link-selected"
      :objects="objects"
      :responsibilities="options"
      @done="selection = []"
    />
  </div>
</template>

<style scoped>
.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.selected {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
  margin-bottom: var(--pnmt-space-2);
}

.through {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  margin-right: var(--pnmt-space-3);
}

.small {
  font-size: 0.857rem;
}
</style>
