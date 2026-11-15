<script setup lang="ts">
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import { computed } from 'vue'
import { useSensorTypes, typeLabel } from '@/composables/useLookups'
import { useSession } from '@/stores/session'
import { caseKinds } from '@/views/cookie/cookieFormat'
import LocationPicker from '@/views/locations/LocationPicker.vue'
import { useLocations } from '@/views/locations/useLocations'
import { withUnknown, type Option } from './cookieAdmin'
import { useHostOptions } from './usePickers'
import { useRedmineLookups } from './useSection'

/**
 * What a narrower instruction or a recipient rule applies to (docs/cookie-plan.md §8, §9.3): a location, a host, a
 * Redmine project, a sensor type or a kind of case, by its id or name as the server compares them. A list the reader
 * may not load leaves a text field. Emits the chosen option's label, for the instruction's own.
 */
const model = defineModel<string | null>({ required: true })
const props = defineProps<{
  scope: 'Everything' | 'Location' | 'Host' | 'Project' | 'SensorType' | 'CaseKind'
  inputId?: string
  invalid?: boolean
}>()
const emit = defineEmits<{ picked: [label: string] }>()
const session = useSession()

const hosts = useHostOptions(computed(() => props.scope === 'Host'))
const redmine = useRedmineLookups(computed(() => props.scope === 'Project'))
const sensorTypes = session.can('Monitoring', 'View') ? useSensorTypes() : null
const locations = session.can('Locations', 'View') ? useLocations() : null

const options = computed<Option[] | null>(() => {
  switch (props.scope) {
    case 'Host':
      return hosts.listed.value ? withUnknown(hosts.options.value, [model.value], hosts.unknown) : null
    case 'Project':
      return withUnknown(
        (redmine.data.value?.projects ?? []).map((p) => ({ label: p.name, value: String(p.id) })),
        [model.value],
        (v) => `Project #${v}`,
      )
    case 'SensorType':
      return sensorTypes
        ? withUnknown(
            sensorTypes.list.value.map((t) => ({ label: typeLabel(t.type), value: t.type })),
            [model.value],
            (v) => v,
          )
        : null
    case 'CaseKind':
      return caseKinds.filter((k) => k !== 'Request' && k !== 'Access').map((k) => ({ label: k, value: k }))
    default:
      return null
  }
})

function pick(next: string | null) {
  model.value = next
  const label =
    props.scope === 'Location'
      ? locations?.data.value?.items.find((l) => l.id === next)?.path
      : options.value?.find((o) => o.value === next)?.label
  if (label) emit('picked', label)
}
</script>

<template>
  <span v-if="scope === 'Everything'" class="muted">–</span>
  <LocationPicker
    v-else-if="scope === 'Location' && locations"
    :model-value="model"
    :input-id="inputId"
    :invalid="invalid"
    none-label="Choose a location"
    size="small"
    @update:model-value="pick"
  />
  <Select
    v-else-if="options"
    :model-value="model"
    :options="options"
    option-label="label"
    option-value="value"
    filter
    :input-id="inputId"
    :invalid="invalid"
    placeholder="Choose"
    size="small"
    class="picker"
    @update:model-value="pick"
  />
  <InputText
    v-else
    :id="inputId"
    :model-value="model ?? ''"
    :invalid="invalid"
    size="small"
    class="mono"
    :placeholder="scope === 'SensorType' ? 'PING' : 'Its id'"
    @update:model-value="(v) => (model = v ?? '')"
  />
</template>

<style scoped>
.picker {
  min-width: 12rem;
}
</style>
