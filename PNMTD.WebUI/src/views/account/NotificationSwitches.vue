<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { useNotify } from '@/composables/useNotify'

/**
 * One's own notification choices (docs/knowledge-base-extensions-plan.md §10.2): for each kind one's rights allow, whether it
 * reaches one by e-mail. Without an address on the account, they wait for one. Each change is saved at once.
 */
const queryClient = useQueryClient()
const notify = useNotify()
const key = [...keys.account, 'notifications'] as const
const me = useQuery({ queryKey: key, queryFn: api.account.notifications })
const saving = ref(false)
const value = computed(() => me.data.value)

async function set(kind: string, on: boolean) {
  saving.value = true
  try {
    const saved = await api.account.saveNotifications({ items: [{ kind, channel: 'Email', enabled: on }] })
    queryClient.setQueryData(key, saved)
    notify.success('Saved')
  } catch (e) {
    notify.error(e, 'Not saved')
  } finally {
    saving.value = false
  }
}

const inputId = (kind: string) => `notify-${kind.replace(/[^a-z0-9]+/gi, '-')}`
</script>

<template>
  <section id="notifications" class="panel" aria-labelledby="notifications-title">
    <div id="notifications-title" class="panel-header">Notifications</div>
    <div class="panel-body body">
      <ErrorState v-if="me.isError.value" :error="me.error.value" what="your notifications" @retry="me.refetch()" />
      <template v-else-if="value">
        <p v-if="!value.kinds.length" class="muted none">Nothing your rights cover sends notifications yet.</p>
        <template v-else>
          <p class="muted lead">E-mail me when…</p>
          <p v-if="!value.hasEmail" class="muted small">Add an e-mail address under Contact to get them.</p>
          <p v-else-if="!value.enabled" class="muted small">
            PNMT sends none at the moment (UserNotifications__Enabled).
          </p>
          <div v-for="k in value.kinds" :key="k.kind" class="switch">
            <ToggleSwitch
              :model-value="k.channels[0]?.enabled ?? false"
              :input-id="inputId(k.kind)"
              :disabled="saving || !value.hasEmail"
              @update:model-value="(v: boolean) => set(k.kind, v)"
            />
            <div>
              <label :for="inputId(k.kind)" class="label">{{ k.label }}</label>
              <div class="muted small">{{ k.help }}</div>
            </div>
          </div>
          <p class="muted small">
            One e-mail per page for {{ value.batchMinutes }} minutes of them, and at most {{ value.maxPerDay }} a day.
          </p>
        </template>
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

.lead,
.none,
.small {
  margin: 0;
}
</style>
