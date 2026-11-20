<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { useNotify } from '@/composables/useNotify'

/**
 * One's own switches for the lounge (docs/lounge-plan.md §9.4): whether Cookie and Seraph may talk about one, and whether
 * they may send one an e-mail from it. Both on by default. Off, the voices call one "a colleague", and the night forgets
 * what they remembered of one; no lounge e-mail reaches one. Each change is saved at once.
 */
const queryClient = useQueryClient()
const notify = useNotify()
const key = [...keys.lounge, 'me'] as const
const me = useQuery({ queryKey: key, queryFn: api.account.lounge })
const saving = ref(false)
const value = computed(() => me.data.value)

async function set(field: keyof S['LoungeSwitches'], on: boolean) {
  if (!value.value) return
  saving.value = true
  try {
    const saved = await api.account.saveLounge({ ...value.value, [field]: on })
    queryClient.setQueryData(key, saved)
    notify.success('Saved')
  } catch (e) {
    notify.error(e, 'Not saved')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <section class="panel" aria-labelledby="lounge-switches-title">
    <div id="lounge-switches-title" class="panel-header">Lounge</div>
    <div class="panel-body body">
      <ErrorState v-if="me.isError.value" :error="me.error.value" what="your lounge settings" @retry="me.refetch()" />
      <template v-else-if="value">
        <div class="switch">
          <ToggleSwitch
            :model-value="value.mentions"
            input-id="lounge-mentions"
            :disabled="saving"
            @update:model-value="(v: boolean) => set('mentions', v)"
          />
          <div>
            <label for="lounge-mentions" class="label">Cookie and Seraph may talk about me</label>
            <div class="muted small">Off, they call you "a colleague", and forget what they remembered of you.</div>
          </div>
        </div>
        <div class="switch">
          <ToggleSwitch
            :model-value="value.emails"
            input-id="lounge-emails"
            :disabled="saving"
            @update:model-value="(v: boolean) => set('emails', v)"
          />
          <div>
            <label for="lounge-emails" class="label">Cookie and Seraph may e-mail me</label>
            <div class="muted small">A few light e-mails from the lounge, reviewed, and at most one a week.</div>
          </div>
        </div>
      </template>
    </div>
  </section>
</template>

<style scoped>
.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.switch {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-2);
}

.label {
  font-weight: 600;
}

.small {
  font-size: 0.857rem;
}
</style>
