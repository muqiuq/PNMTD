<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Select from 'primevue/select'
import { computed } from 'vue'
import { personLabel, responsibilitiesApi, responsibilityKeys, type ResponsibilityPerson } from '@/api/responsibilities'

/**
 * Whom a responsibility names (docs/responsibilities-plan.md §4.2): the active users, by name. The current choice stays readable
 * when it is no longer active (it stays set); nobody is a choice too. Only those with Responsibilities: Manage pick.
 */
const model = defineModel<string | null>({ required: true })
const props = defineProps<{
  inputId: string
  /** The choice as saved, so that an inactive person still shows by name. */
  current?: ResponsibilityPerson | null
  invalid?: boolean
  placeholder?: string
}>()

const people = useQuery({ queryKey: responsibilityKeys.people, queryFn: responsibilitiesApi.people, staleTime: 60_000 })
const options = computed(() => {
  const list = (people.data.value?.users ?? []).map((u) => ({ id: u.id, label: u.name, detail: u.username }))
  const current = props.current
  if (current && !list.some((o) => o.id === current.id)) {
    list.unshift({ id: current.id, label: personLabel(current), detail: '' })
  }
  return list
})
</script>

<template>
  <Select
    v-model="model"
    :input-id="inputId"
    :options="options"
    option-label="label"
    option-value="id"
    :placeholder="placeholder ?? 'Nobody'"
    :loading="people.isLoading.value"
    :invalid="invalid"
    filter
    show-clear
    fluid
  >
    <template #option="{ option }">
      <span>{{ option.label }}</span>
      <span v-if="option.detail" class="muted detail">{{ option.detail }}</span>
    </template>
  </Select>
</template>

<style scoped>
.detail {
  margin-left: var(--pnmt-space-2);
  font-size: 0.857rem;
}
</style>
