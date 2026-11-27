<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Chip from 'primevue/chip'
import { computed } from 'vue'
import { kindLabels, licenseKeys, licensesApi, vendorLabel, type LicenseRow, type LicenseType } from '@/api/licenses'
import { moreSet, without, type ListState, type MoreKey } from './licenceList'

/**
 * The filters set under *More filters* (docs/licences-plan.md §8.3), each a chip that removes it: a link from the overview
 * (?unusedSeats=1, ?pastEol=1, …) says so above the rows it narrowed.
 */
const state = defineModel<ListState>('state', { required: true })
const props = defineProps<{ type: LicenseType; rows: readonly LicenseRow[]; hostName: string | null }>()

const categories = useQuery({ queryKey: licenseKeys.categories, queryFn: licensesApi.categories, staleTime: 60_000 })
const templates = useQuery({ queryKey: licenseKeys.templates, queryFn: licensesApi.templates, staleTime: 60_000 })

function label(key: MoreKey): string {
  const s = state.value
  switch (key) {
    case 'kind':
      return `Kind: ${s.kind.map((k) => kindLabels[k]).join(', ')}`
    case 'category':
      return `Category: ${categories.data.value?.find((c) => c.id === s.category)?.name ?? '…'}`
    case 'manufacturer':
      return `Manufacturer: ${s.manufacturer}`
    case 'vendor':
      return `${vendorLabel(props.type)}: ${s.vendor}`
    case 'template':
      return `Template: ${templates.data.value?.find((t) => t.id === s.template)?.name ?? '…'}`
    case 'costCenter':
      return `Cost centre: ${s.costCenter}`
    case 'location':
      return `Location: ${props.rows.find((r) => r.locationId === s.location)?.locationName ?? 'one with none here'}`
    case 'owner':
      return s.owner === 'mine'
        ? 'Owner: me'
        : `Owner: ${props.rows.find((r) => r.ownerId === s.owner)?.ownerName ?? 'someone who owns none here'}`
    case 'host':
      return props.hostName ? `On ${props.hostName}` : 'On one host'
    case 'noHost':
      return 'On no host'
    case 'nobody':
      return 'Nobody notified'
    case 'expires':
      return `Expires within ${s.expires} days`
    case 'reviewDue':
      return 'Review due'
    case 'noPrice':
      return 'Without a price'
    case 'unusedSeats':
      return 'Unused seats'
    case 'pastEol':
      return 'Past its end of life'
  }
}
const chips = computed(() => moreSet(state.value).map((key) => ({ key, label: label(key) })))
</script>

<template>
  <div v-if="chips.length" class="chips" aria-label="Filters set" data-test="filter-chips">
    <Chip v-for="c in chips" :key="c.key" :label="c.label" removable @remove="state = without(state, c.key)" />
  </div>
</template>

<style scoped>
.chips {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}
</style>
