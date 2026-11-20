<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import ProgressSpinner from 'primevue/progressspinner'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { useNotify } from '@/composables/useNotify'

/**
 * A person's switches for the lounge (docs/lounge-plan.md §9.4, §15.4), for admins: whether Cookie and Seraph may talk
 * about them, and whether they may e-mail them from it. Each person sets their own on Account too. Each change is saved
 * at once, and audited.
 */
const props = defineProps<{ userId: string; username: string }>()
const queryClient = useQueryClient()
const notify = useNotify()
const key = computed(() => [...keys.users, props.userId, 'lounge'])
const switches = useQuery({ queryKey: key, queryFn: () => api.users.lounge(props.userId) })
const saving = ref(false)

async function set(field: keyof S['LoungeSwitches'], on: boolean) {
  const value = switches.data.value
  if (!value) return
  saving.value = true
  try {
    const saved = await api.users.saveLounge(props.userId, { ...value, [field]: on })
    queryClient.setQueryData(key.value, saved)
    notify.success(`${props.username} saved`)
  } catch (e) {
    notify.error(e, 'Not saved')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <ErrorState
    v-if="switches.isError.value && !switches.data.value"
    :error="switches.error.value"
    what="the lounge's switches"
    @retry="switches.refetch()"
  />
  <div v-else-if="!switches.data.value" class="center"><ProgressSpinner style="width: 24px; height: 24px" /></div>
  <div v-else class="form">
    <div class="switch">
      <ToggleSwitch
        :model-value="switches.data.value.mentions"
        input-id="ul-mentions"
        :disabled="saving"
        @update:model-value="(v: boolean) => set('mentions', v)"
      />
      <div>
        <label for="ul-mentions">Cookie and Seraph may talk about them</label>
        <small class="field-hint"
          >Off, the voices call them "a colleague", and forget what they remembered of them.</small
        >
      </div>
    </div>
    <div class="switch">
      <ToggleSwitch
        :model-value="switches.data.value.emails"
        input-id="ul-emails"
        :disabled="saving"
        @update:model-value="(v: boolean) => set('emails', v)"
      />
      <div>
        <label for="ul-emails">Cookie and Seraph may e-mail them</label>
        <small class="field-hint">A few light e-mails from the lounge, reviewed, and at most one a week.</small>
      </div>
    </div>
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-3);
}

.switch {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-2);
}

.switch > div {
  display: flex;
  flex-direction: column;
}
</style>
