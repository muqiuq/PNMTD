<script setup lang="ts">
import AutoComplete from 'primevue/autocomplete'
import { ref } from 'vue'
import { licensesApi, type LicenseTarget } from '@/api/licenses'
import { useNotify } from '@/composables/useNotify'

/**
 * A host to assign or a sensor to link (docs/licences-plan.md §5.1, §5.4), found by name through the licences' own search (Edit), so
 * that neither needs Monitoring: View. Those already on the item are marked. Picking one emits it and clears the field.
 */
const props = defineProps<{ kind: 'host' | 'sensor'; licenseId: string; inputId: string; placeholder?: string }>()
const emit = defineEmits<{ pick: [target: LicenseTarget] }>()
const notify = useNotify()
const text = ref<string | LicenseTarget>('')
const suggestions = ref<LicenseTarget[]>([])

async function complete(event: { query: string }) {
  try {
    suggestions.value = await licensesApi.targets(props.kind, event.query.trim(), props.licenseId)
  } catch (e) {
    suggestions.value = []
    notify.error(e, props.kind === 'host' ? 'Hosts not found' : 'Sensors not found')
  }
}

function pick(event: { value: LicenseTarget }) {
  emit('pick', event.value)
  text.value = ''
}
</script>

<template>
  <AutoComplete
    v-model="text"
    :input-id="inputId"
    :suggestions="suggestions"
    option-label="name"
    :placeholder="placeholder ?? (kind === 'host' ? 'Search hosts by name' : 'Search sensors by name or host')"
    :delay="200"
    dropdown
    fluid
    @complete="complete"
    @option-select="pick"
  >
    <template #option="{ option }">
      <span class="option">
        <i :class="kind === 'host' ? 'pi pi-server' : 'pi pi-wave-pulse'" class="muted" aria-hidden="true" />
        <span class="name">{{ option.name }}</span>
        <span v-if="option.hostName" class="muted">on {{ option.hostName }}</span>
        <span v-if="option.locationName" class="muted">· {{ option.locationName }}</span>
        <span v-if="option.assigned" class="assigned">{{ kind === 'host' ? 'assigned' : 'linked' }}</span>
      </span>
    </template>
  </AutoComplete>
</template>

<style scoped>
.option {
  display: inline-flex;
  align-items: baseline;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.name {
  font-weight: 500;
}

.assigned {
  font-size: 0.786rem;
  padding: 0 0.4rem;
  border-radius: 999px;
  background: var(--pnmt-none-soft);
}
</style>
