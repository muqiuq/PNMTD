<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Select from 'primevue/select'
import { computed } from 'vue'
import { licenseKeys, licensesApi } from '@/api/licenses'

/**
 * An item's owner (docs/licences-plan.md §3.3, §5.5): the active users, by name, never their contact details; nobody is a choice too.
 * The owner as saved stays readable by name when no longer among them.
 */
const model = defineModel<string | null | undefined>({ required: true })
const props = defineProps<{
  inputId: string
  /** The owner as saved, so that an inactive one still shows by name. */
  current?: { id: string; name: string | null } | null
  invalid?: boolean
}>()

const people = useQuery({ queryKey: licenseKeys.people, queryFn: licensesApi.people, staleTime: 60_000 })
const options = computed(() => {
  const list = (people.data.value ?? []).map((p) => ({ id: p.id, label: p.name, detail: p.username }))
  const current = props.current
  if (current && !list.some((o) => o.id === current.id))
    list.unshift({ id: current.id, label: current.name ?? '–', detail: '' })
  return list
})
const value = computed({
  get: () => model.value ?? null,
  set: (v: string | null) => (model.value = v ?? null),
})
</script>

<template>
  <Select
    v-model="value"
    :input-id="inputId"
    :options="options"
    option-label="label"
    option-value="id"
    placeholder="Nobody"
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
