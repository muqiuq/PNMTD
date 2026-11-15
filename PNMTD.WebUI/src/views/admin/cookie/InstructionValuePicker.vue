<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { api, keys } from '@/api/resources'
import { typeLabel, useSensorTypes } from '@/composables/useLookups'
import { useSession } from '@/stores/session'
import {
  suppressCaseKindLabel,
  suppressCaseKinds,
  suppressChannelLabel,
  suppressChannels,
  type InstructionScope,
} from '@/views/cookie/acting'
import LocationPicker from '@/views/locations/LocationPicker.vue'
import { withUnknown, type Option } from './cookieAdmin'
import { useHostOptions } from './usePickers'

/**
 * What a mute or a suppress applies to (docs/cookie-plan.md §10.3): a host, a sensor, a location or a case by its id; a
 * channel, a sensor type or a kind of case by name. Pickers where the reader may load the list (hosts and sensors with
 * Monitoring: View, locations with Locations: View), a text field for the id otherwise. Emits the chosen option's
 * label, for the instruction's own.
 */
const model = defineModel<string | null>({ required: true })
const props = defineProps<{ scope: InstructionScope; label: string; inputId?: string; invalid?: boolean }>()
const emit = defineEmits<{ picked: [label: string] }>()
const session = useSession()
const monitoring = computed(() => session.can('Monitoring', 'View'))

const hosts = useHostOptions(computed(() => props.scope === 'host'))
const sensorTypes = monitoring.value ? useSensorTypes() : null
const locations = computed(() => session.can('Locations', 'View'))

// Sensors: searched on the server as one types, the chosen one kept in the list.
const search = ref('')
const term = ref('')
let timer: ReturnType<typeof setTimeout> | undefined
watch(search, (value) => {
  clearTimeout(timer)
  timer = setTimeout(() => (term.value = value.trim()), 250)
})
const sensors = useQuery({
  queryKey: computed(() => [...keys.sensors, 'for-instructions', term.value]),
  queryFn: () => api.sensors.list({ search: term.value || undefined, pageSize: 50, sort: 'name' }),
  enabled: computed(() => props.scope === 'sensor' && monitoring.value),
  staleTime: 30_000,
})
const cases = useQuery({
  queryKey: [...keys.cookie, 'cases', 'for-instructions'],
  queryFn: () => api.cookie.cases({ open: true, pageSize: 200 }),
  enabled: computed(() => props.scope === 'case'),
  staleTime: 30_000,
})
const chosenLabel = ref<string | null>(null)
watch(
  () => props.scope,
  () => (chosenLabel.value = null),
)

const options = computed<Option[] | null>(() => {
  const keep = (list: Option[], unknown: string) => withUnknown(list, [model.value], () => chosenLabel.value ?? unknown)
  switch (props.scope) {
    case 'host':
      return hosts.listed.value ? keep(hosts.options.value, 'A removed host') : null
    case 'sensor':
      return monitoring.value
        ? keep(
            (sensors.data.value?.items ?? []).map((s) => ({ label: `${s.host.name} / ${s.name}`, value: s.id })),
            'A sensor',
          )
        : null
    case 'case':
      return keep(
        (cases.data.value?.items ?? []).map((c) => ({ label: c.title, value: c.id })),
        'A case',
      )
    case 'channel':
      return suppressChannels.map((c) => ({ label: suppressChannelLabel(c), value: c }))
    case 'sensor-type':
      return sensorTypes
        ? keep(
            sensorTypes.list.value.map((t) => ({ label: typeLabel(t.type), value: t.type })),
            model.value ?? '',
          )
        : null
    case 'case-kind':
      return suppressCaseKinds.map((k) => ({ label: suppressCaseKindLabel(k), value: k }))
    default:
      return null
  }
})

function pick(next: string | null) {
  model.value = next
  const name = options.value?.find((o) => o.value === next)?.label
  if (name) {
    chosenLabel.value = name
    emit('picked', name)
  }
}
</script>

<template>
  <LocationPicker
    v-if="scope === 'location' && locations"
    :model-value="model"
    :input-id="inputId"
    :aria-label="label"
    :invalid="invalid"
    none-label="Choose a location"
    @update:model-value="(v: string | null) => pick(v)"
  />
  <Select
    v-else-if="options"
    :model-value="model"
    :options="options"
    option-label="label"
    option-value="value"
    :filter="scope !== 'channel' && scope !== 'case-kind'"
    :loading="(scope === 'sensor' && sensors.isFetching.value) || (scope === 'case' && cases.isPending.value)"
    :input-id="inputId"
    :aria-label="label"
    :invalid="invalid"
    placeholder="Choose"
    class="picker"
    @filter="(e: { value: string }) => scope === 'sensor' && (search = e.value)"
    @update:model-value="(v: string | null) => pick(v)"
  />
  <InputText
    v-else
    :id="inputId"
    :model-value="model ?? ''"
    :aria-label="label"
    :invalid="invalid"
    class="mono picker"
    :placeholder="scope === 'sensor-type' ? 'PING' : 'Its id'"
    @update:model-value="(v) => (model = v ?? '')"
  />
</template>

<style scoped>
.picker {
  width: min(24rem, 100%);
}
</style>
