<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import AutoComplete from 'primevue/autocomplete'
import { computed, ref } from 'vue'
import { licenseKeys, licensesApi, type LicenseNames } from '@/api/licenses'

/**
 * A manufacturer, vendor, cost centre or seat unit (docs/licences-plan.md §8.6): free text, with every name in use as a suggestion,
 * so that one name isn't typed three ways. Renaming and merging is the Templates tab's *Names*.
 */
const model = defineModel<string | null | undefined>({ required: true })
const props = defineProps<{
  field: keyof LicenseNames
  inputId: string
  invalid?: boolean
  placeholder?: string
  disabled?: boolean
}>()
const names = useQuery({ queryKey: licenseKeys.names, queryFn: licensesApi.names, staleTime: 60_000 })
const all = computed(() => (names.data.value?.[props.field] ?? []).map((n) => n.name))
const suggestions = ref<string[]>([])
function complete(event: { query: string }) {
  const q = event.query.trim().toLowerCase()
  suggestions.value = all.value.filter((n) => !q || n.toLowerCase().includes(q)).slice(0, 20)
}
const text = computed({
  get: () => model.value ?? '',
  set: (value: string | null) => {
    model.value = value ? value : null
  },
})
</script>

<template>
  <AutoComplete
    v-model="text"
    :input-id="inputId"
    :suggestions="suggestions"
    :invalid="invalid"
    :placeholder="placeholder"
    :disabled="disabled"
    dropdown
    fluid
    @complete="complete"
  />
</template>
