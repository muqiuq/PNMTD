<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import AutoComplete from 'primevue/autocomplete'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import MultiSelect from 'primevue/multiselect'
import Popover from 'primevue/popover'
import Select from 'primevue/select'
import { computed, ref } from 'vue'
import {
  kindLabels,
  kindsOf,
  licenseKeys,
  licensesApi,
  vendorLabel,
  type LicenseRow,
  type LicenseTarget,
  type LicenseType,
} from '@/api/licenses'
import { collator, emptyList, expiresWindows, moreKeys, moreSet, type ListState } from './licenceList'

/**
 * *More filters* of the Licences, Services and Hardware tabs (docs/licences-plan.md §8.3), in a popover so the toolbar stays one
 * row: kind, category, manufacturer, vendor or provider, template, cost centre, location, owner (*mine* first), host, and the
 * lists the overview links to. The names, locations and owners offered are the ones the type's rows use. Hosts are found by name
 * for Edit (the targets' search); others reach ?host from a host's page.
 */
const state = defineModel<ListState>('state', { required: true })
const props = defineProps<{ type: LicenseType; rows: readonly LicenseRow[]; edit: boolean }>()
const emit = defineEmits<{ host: [id: string, name: string] }>()
const panel = ref<InstanceType<typeof Popover> | null>(null)
const count = computed(() => moreSet(state.value).length)

/** A filter as a v-model: each change is a new state, so the URL follows. */
function field<K extends keyof ListState>(key: K) {
  return computed({
    get: () => state.value[key],
    set: (value: ListState[K]) => (state.value = { ...state.value, [key]: value }),
  })
}
const kind = field('kind')
const category = field('category')
const manufacturer = field('manufacturer')
const vendor = field('vendor')
const template = field('template')
const costCenter = field('costCenter')
const location = field('location')
const owner = field('owner')
const noHost = field('noHost')
const nobody = field('nobody')
const expires = field('expires')
const reviewDue = field('reviewDue')
const noPrice = field('noPrice')
const unusedSeats = field('unusedSeats')
const pastEol = field('pastEol')

const categories = useQuery({ queryKey: licenseKeys.categories, queryFn: licensesApi.categories, staleTime: 60_000 })
const templates = useQuery({ queryKey: licenseKeys.templates, queryFn: licensesApi.templates, staleTime: 60_000 })

type Option = { label: string; value: string }
const byLabel = (a: Option, b: Option) => collator.compare(a.label, b.label)
function distinct(pick: (r: LicenseRow) => [string | null, string | null]): Option[] {
  const seen = new Map<string, string>()
  for (const r of props.rows) {
    const [value, label] = pick(r)
    if (value && !seen.has(value)) seen.set(value, label ?? value)
  }
  return [...seen].map(([value, label]) => ({ value, label })).sort(byLabel)
}

const kinds = computed(() => kindsOf[props.type].map((k) => ({ value: k, label: kindLabels[k] })))
const categoryOptions = computed(() => (categories.data.value ?? []).map((c) => ({ value: c.id, label: c.name })))
const templateOptions = computed(() =>
  (templates.data.value ?? []).filter((t) => t.type === props.type).map((t) => ({ value: t.id, label: t.name })),
)
const manufacturers = computed(() => distinct((r) => [r.manufacturer, r.manufacturer]))
const vendors = computed(() => distinct((r) => [r.vendor, r.vendor]))
const costCenters = computed(() => distinct((r) => [r.costCenter, r.costCenter]))
const locations = computed(() => distinct((r) => [r.locationId, r.locationName]))
const owners = computed(() => [{ value: 'mine', label: 'Mine' }, ...distinct((r) => [r.ownerId, r.ownerName])])
const windows = expiresWindows.map((d) => ({ value: d, label: `${d} days` }))

// A host by name (Edit: the targets' search, without Monitoring: View).
const hostText = ref<string | LicenseTarget | null>(null)
const hostSuggestions = ref<LicenseTarget[]>([])
async function completeHost(event: { query: string }) {
  hostSuggestions.value = await licensesApi.targets('host', event.query.trim()).catch(() => [])
}
function pickHost(event: { value: LicenseTarget }) {
  emit('host', event.value.id, event.value.name)
  state.value = { ...state.value, host: event.value.id, noHost: false }
  hostText.value = null
}

function clearMore() {
  const empty = emptyList()
  const next = { ...state.value }
  for (const key of moreKeys) (next as Record<string, unknown>)[key] = empty[key]
  state.value = next
}
</script>

<template>
  <Button
    :label="count ? `More filters (${count})` : 'More filters'"
    icon="pi pi-sliders-h"
    size="small"
    severity="secondary"
    :outlined="!count"
    aria-haspopup="dialog"
    data-test="more-filters"
    @click="(e: Event) => panel?.toggle(e)"
  />
  <Popover ref="panel">
    <div class="more" role="group" aria-label="More filters">
      <div class="grid">
        <div class="field">
          <label for="lf-kind">Kind</label>
          <MultiSelect
            v-model="kind"
            input-id="lf-kind"
            :options="kinds"
            option-label="label"
            option-value="value"
            placeholder="Any kind"
            size="small"
            :max-selected-labels="2"
            fluid
          />
        </div>
        <div class="field">
          <label for="lf-category">Category</label>
          <Select
            v-model="category"
            input-id="lf-category"
            :options="categoryOptions"
            option-label="label"
            option-value="value"
            placeholder="Any category"
            show-clear
            size="small"
            fluid
          />
        </div>
        <div v-if="type !== 'Service'" class="field">
          <label for="lf-manufacturer">Manufacturer</label>
          <Select
            v-model="manufacturer"
            input-id="lf-manufacturer"
            :options="manufacturers"
            option-label="label"
            option-value="value"
            placeholder="Any manufacturer"
            show-clear
            filter
            size="small"
            fluid
          />
        </div>
        <div class="field">
          <label for="lf-vendor">{{ vendorLabel(type) }}</label>
          <Select
            v-model="vendor"
            input-id="lf-vendor"
            :options="vendors"
            option-label="label"
            option-value="value"
            :placeholder="`Any ${vendorLabel(type).toLowerCase()}`"
            show-clear
            filter
            size="small"
            fluid
          />
        </div>
        <div class="field">
          <label for="lf-template">Template</label>
          <Select
            v-model="template"
            input-id="lf-template"
            :options="templateOptions"
            option-label="label"
            option-value="value"
            placeholder="Any template"
            show-clear
            filter
            size="small"
            fluid
          />
        </div>
        <div class="field">
          <label for="lf-cost-centre">Cost centre</label>
          <Select
            v-model="costCenter"
            input-id="lf-cost-centre"
            :options="costCenters"
            option-label="label"
            option-value="value"
            placeholder="Any cost centre"
            show-clear
            size="small"
            fluid
          />
        </div>
        <div class="field">
          <label for="lf-location">Location</label>
          <Select
            v-model="location"
            input-id="lf-location"
            :options="locations"
            option-label="label"
            option-value="value"
            placeholder="Any location"
            show-clear
            filter
            size="small"
            fluid
          />
        </div>
        <div class="field">
          <label for="lf-owner">Owner</label>
          <Select
            v-model="owner"
            input-id="lf-owner"
            :options="owners"
            option-label="label"
            option-value="value"
            placeholder="Anyone"
            show-clear
            filter
            size="small"
            fluid
          />
        </div>
        <div class="field">
          <label for="lf-expires">Expires within</label>
          <Select
            v-model="expires"
            input-id="lf-expires"
            :options="windows"
            option-label="label"
            option-value="value"
            placeholder="Any time"
            show-clear
            size="small"
            fluid
          />
        </div>
        <div v-if="edit" class="field">
          <label for="lf-host">On the host</label>
          <AutoComplete
            v-model="hostText"
            input-id="lf-host"
            :suggestions="hostSuggestions"
            option-label="name"
            placeholder="A host's name"
            size="small"
            :delay="250"
            fluid
            @complete="completeHost"
            @option-select="pickHost"
          >
            <template #option="{ option }">
              <span>{{ option.name }}</span>
              <span v-if="option.locationName" class="muted"> · {{ option.locationName }}</span>
            </template>
          </AutoComplete>
        </div>
      </div>
      <div class="checks">
        <label class="check"><Checkbox v-model="noHost" binary input-id="lf-no-host" /><span>On no host</span></label>
        <label class="check"
          ><Checkbox v-model="nobody" binary input-id="lf-nobody" /><span>Nobody notified</span></label
        >
        <label class="check"><Checkbox v-model="reviewDue" binary input-id="lf-review" /><span>Review due</span></label>
        <label class="check"
          ><Checkbox v-model="noPrice" binary input-id="lf-no-price" /><span>Without a price</span></label
        >
        <label class="check"
          ><Checkbox v-model="unusedSeats" binary input-id="lf-unused" /><span>Unused seats</span></label
        >
        <label class="check"
          ><Checkbox v-model="pastEol" binary input-id="lf-past-eol" /><span>Past its end of life</span></label
        >
      </div>
      <div class="actions">
        <Button v-if="count" label="Clear these" size="small" severity="secondary" text @click="clearMore" />
        <Button label="Done" size="small" @click="panel?.hide()" />
      </div>
    </div>
  </Popover>
</template>

<style scoped>
.more {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  width: min(36rem, 90vw);
}

.grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--pnmt-space-2) var(--pnmt-space-3);
}

.checks {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--pnmt-space-2) var(--pnmt-space-3);
}

.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--pnmt-space-2);
}

@media (max-width: 599px) {
  .grid,
  .checks {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
