<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { kbLinkIcon, kbLinkRoute } from '@/api/kb'
import {
  linkKindLabel,
  linkObjectLabel,
  responsibilitiesApi,
  responsibilityKeys,
  type ResponsibilityLinkRow,
} from '@/api/responsibilities'
import ColourDot from '@/components/ColourDot.vue'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import { useActions } from '@/composables/useActions'
import { useNow } from '@/composables/useNow'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute, formatRelative } from '@/format'
import { useSession } from '@/stores/session'
import LocationPicker from '@/views/locations/LocationPicker.vue'
import BulkLinkDialog, { type BulkMode } from './BulkLinkDialog.vue'
import { linksToQuery, type LinksState } from './linksState'

/**
 * Every link of every responsibility, one row each (docs/responsibilities-extensions-plan.md §5.1): filtered by responsibility, person,
 * kind and location, searched in the names and the Where, paged on the server. With Edit: Link…, ✕ on a row, and for the rows
 * selected Move to…, Also link to… and Unlink. The selection holds across pages until a filter changes.
 */
defineProps<{ edit: boolean }>()
const state = defineModel<LinksState>('state', { required: true })
const session = useSession()
const now = useNow()
const { run, done, ask } = useActions()

const all = useQuery({ queryKey: responsibilityKeys.list({}), queryFn: () => responsibilitiesApi.list({}) })
const options = computed(() =>
  (all.data.value?.items ?? []).map((r) => ({ value: r.id, label: r.name, color: r.color ?? null })),
)
/** Everyone a responsibility names, from the list: no right beyond Responsibilities: View. */
const people = computed(() => {
  const byId = new Map<string, string>()
  for (const r of all.data.value?.items ?? []) for (const p of [r.responsible, r.deputy]) if (p) byId.set(p.id, p.name)
  return [...byId]
    .map(([value, label]) => ({ value, label }))
    .sort((a, b) => a.label.localeCompare(b.label, undefined, { sensitivity: 'base' }))
})
const kindChoices = [
  { label: 'Pages', value: 'Page' },
  { label: 'Locations', value: 'Location' },
  { label: 'Hosts', value: 'Host' },
]
// The location filter lists the tree, which needs Locations: View.
const canLocations = computed(() => session.can('Locations', 'View'))

const filters = computed(() => ({
  responsibility: state.value.responsibilities.length ? state.value.responsibilities : undefined,
  person: state.value.person ?? undefined,
  kind: state.value.kinds.length ? state.value.kinds : undefined,
  location: state.value.location ?? undefined,
  below: state.value.location && !state.value.below ? false : undefined,
}))
const table = useServerTable<ResponsibilityLinkRow>({
  key: [...responsibilityKeys.all, 'links'],
  fetch: (q) => responsibilitiesApi.linkRows(q),
  defaultSort: 'responsibility',
  pageSize: 50,
  filters,
  syncUrl: true,
  urlQuery: computed(() => linksToQuery(state.value)),
})
const active = computed(
  () =>
    (state.value.responsibilities.length ? 1 : 0) +
    (state.value.person ? 1 : 0) +
    (state.value.kinds.length ? 1 : 0) +
    (state.value.location ? 1 : 0),
)

const selection = ref<ResponsibilityLinkRow[]>([])
watch(filters, () => (selection.value = []), { deep: true })
const dialogOpen = ref(false)
const mode = ref<BulkMode>('link')
function openDialog(m: BulkMode) {
  mode.value = m
  dialogOpen.value = true
}

const unlinkOne = (row: ResponsibilityLinkRow) =>
  run(() => responsibilitiesApi.unlink(row.responsibility.id, row.id), {
    success: `${linkObjectLabel(row)} unlinked from ${row.responsibility.name}`,
    failure: 'Not unlinked',
    invalidate: [responsibilityKeys.all],
  })
async function unlinkSelected() {
  const rows = selection.value
  const owners = new Set(rows.map((r) => r.responsibility.id)).size
  const what = `${rows.length} link${rows.length === 1 ? '' : 's'}`
  const from = owners === 1 ? rows[0]!.responsibility.name : `${owners} responsibilities`
  if (!(await ask(`Unlink ${what} from ${from}?`, 'The pages, locations and hosts stay as they are.', 'Unlink'))) return
  if (
    await done(() => responsibilitiesApi.unlinkMany(rows.map((r) => r.id)), {
      success: `Unlinked ${what}`,
      invalidate: [responsibilityKeys.all],
    })
  )
    selection.value = []
}
</script>

<template>
  <div class="tab">
    <FilterBar v-model:search="table.search.value" placeholder="Object, where, responsibility…" :active="active">
      <MultiSelect
        v-model="state.responsibilities"
        :options="options"
        option-label="label"
        option-value="value"
        filter
        placeholder="All responsibilities"
        size="small"
        display="chip"
        class="wide"
        aria-label="Responsibilities"
      >
        <template #option="{ option }">
          <span class="option"><ColourDot :colour="option.color" />{{ option.label }}</span>
        </template>
      </MultiSelect>
      <Select
        v-model="state.person"
        :options="people"
        option-label="label"
        option-value="value"
        filter
        show-clear
        placeholder="Anyone"
        size="small"
        aria-label="Responsible or deputy"
      />
      <MultiSelect
        v-model="state.kinds"
        :options="kindChoices"
        option-label="label"
        option-value="value"
        placeholder="All kinds"
        size="small"
        aria-label="Kinds"
      />
      <template v-if="canLocations">
        <LocationPicker v-model="state.location" none-label="All locations" size="small" aria-label="Location" />
        <label v-if="state.location" class="check">
          <Checkbox v-model="state.below" binary input-id="links-below" />
          <span>and below</span>
        </label>
      </template>
      <template #end>
        <Button
          v-if="edit"
          label="Link…"
          icon="pi pi-plus"
          size="small"
          data-test="bulk-link"
          @click="openDialog('link')"
        />
      </template>
    </FilterBar>
    <div v-if="edit && selection.length" class="selected" role="status" data-test="links-selected">
      <span>{{ selection.length }} selected</span>
      <Button label="Move to…" icon="pi pi-arrow-right" size="small" text @click="openDialog('move')" />
      <Button label="Also link to…" icon="pi pi-clone" size="small" text @click="openDialog('copy')" />
      <Button label="Unlink" icon="pi pi-times" size="small" severity="danger" text @click="unlinkSelected" />
      <Button label="Clear" size="small" severity="secondary" text @click="selection = []" />
    </div>
    <ListTable
      v-model:selection="selection"
      :table="table"
      what="links"
      empty-text="No links match. A responsibility's page links pages, locations and hosts, and so does Link… here."
    >
      <Column v-if="edit" selection-mode="multiple" style="width: 3rem" />
      <Column header="Responsibility" field="responsibility" sortable>
        <template #body="{ data }">
          <span class="name">
            <ColourDot :colour="data.responsibility.color" />
            <RouterLink :to="{ name: 'responsibility', params: { id: data.responsibility.id } }">{{
              data.responsibility.name
            }}</RouterLink>
          </span>
        </template>
      </Column>
      <Column header="Kind" field="kind" sortable style="width: 6rem">
        <template #body="{ data }">
          <i
            :class="kbLinkIcon[data.kind as keyof typeof kbLinkIcon]"
            class="muted"
            role="img"
            :aria-label="linkKindLabel[data.kind as keyof typeof linkKindLabel]"
            :title="linkKindLabel[data.kind as keyof typeof linkKindLabel]"
          />
        </template>
      </Column>
      <Column header="Object" field="object" sortable>
        <template #body="{ data }">
          <RouterLink :to="kbLinkRoute({ kind: data.kind, id: data.targetId, number: data.number })">{{
            linkObjectLabel(data)
          }}</RouterLink>
          <span v-if="data.archived" class="muted tag">archived</span>
        </template>
      </Column>
      <Column header="Where">
        <template #body="{ data }">
          <span class="muted">{{ data.detail }}</span>
        </template>
      </Column>
      <Column header="Linked" field="linked" sortable style="width: 11rem">
        <template #body="{ data }">
          <span class="muted" :title="formatAbsolute(data.createdAt)"
            >{{ data.createdByName ? `${data.createdByName} · ` : '' }}{{ formatRelative(data.createdAt, now) }}</span
          >
        </template>
      </Column>
      <Column v-if="edit" style="width: 3rem">
        <template #body="{ data }">
          <Button
            icon="pi pi-times"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`Unlink ${linkObjectLabel(data)} from ${data.responsibility.name}`"
            @click.stop="unlinkOne(data)"
          />
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="name">
          <ColourDot :colour="item.responsibility.color" />
          <span class="strong">{{ item.responsibility.name }}</span>
        </div>
        <div class="small">
          <i :class="kbLinkIcon[item.kind]" class="muted" aria-hidden="true" />
          <RouterLink :to="kbLinkRoute({ kind: item.kind, id: item.targetId, number: item.number })">{{
            linkObjectLabel(item)
          }}</RouterLink>
          <span v-if="item.detail" class="muted"> · {{ item.detail }}</span>
        </div>
      </template>
    </ListTable>
    <BulkLinkDialog
      v-model:visible="dialogOpen"
      :mode="mode"
      :links="selection"
      :responsibilities="options"
      @done="selection = []"
    />
  </div>
</template>

<style scoped>
.wide {
  min-width: 14rem;
}

.option,
.name {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

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

.tag {
  margin-left: var(--pnmt-space-2);
  font-size: 0.786rem;
}

.small {
  font-size: 0.857rem;
}
</style>
