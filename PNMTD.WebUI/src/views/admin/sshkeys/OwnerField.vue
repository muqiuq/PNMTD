<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, useId, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'

/**
 * A key's owner (docs/linux-sensor-plan.md §6.4): a PNMT user from the list, or a text for keys of nobody in PNMT (a service, a
 * contractor, a vendor's support). One of the two is set; switching clears the other.
 */
const userId = defineModel<string | null>('userId', { required: true })
const owner = defineModel<string>('owner', { required: true })
const uid = useId()
const mode = ref<'user' | 'text'>(owner.value && !userId.value ? 'text' : 'user')
watch([userId, owner], ([u, o]) => {
  if (u) mode.value = 'user'
  else if (o) mode.value = 'text'
})
watch(mode, (m) => {
  if (m === 'user') owner.value = ''
  else userId.value = null
})
const users = useQuery({
  queryKey: [...keys.users, 'all'],
  queryFn: () => api.users.list({ pageSize: 500, sort: 'username' }),
})
const options = computed(() =>
  (users.data.value?.items ?? []).map((u: S['UserItem']) => ({
    label: `${u.displayName || u.username} (${u.username})${u.isActive ? '' : ', deactivated'}`,
    value: u.id,
  })),
)
const modes = [
  { label: 'A PNMT user', value: 'user' },
  { label: 'Someone else', value: 'text' },
]
</script>

<template>
  <div class="field">
    <label :id="`${uid}-owner`">Owner</label>
    <SelectButton
      v-model="mode"
      :options="modes"
      option-label="label"
      option-value="value"
      :allow-empty="false"
      size="small"
      :aria-labelledby="`${uid}-owner`"
    />
    <Select
      v-if="mode === 'user'"
      v-model="userId"
      :options="options"
      option-label="label"
      option-value="value"
      filter
      placeholder="Choose a user"
      aria-label="Owner (user)"
      :loading="users.isLoading.value"
    />
    <InputText
      v-else
      v-model="owner"
      maxlength="200"
      placeholder="A service, a contractor, a vendor's support"
      aria-label="Owner (text)"
    />
  </div>
</template>
