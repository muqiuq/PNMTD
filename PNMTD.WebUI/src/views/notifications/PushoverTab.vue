<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { useSession } from '@/stores/session'
import {
  draftOf,
  importanceRows,
  MAX_EXPIRE,
  MAX_RETRY,
  MIN_RETRY,
  pushoverErrors,
  pushoverPriorities,
  requestOf,
  usesEmergency,
  type PushoverDraft,
} from './pushoverPriorities'

/**
 * The Pushover tab (docs/cookie-object-notes-plan.md §5.4): the priority each importance gets on the phone, and how an
 * emergency repeats. Everyone on the manager reads it; Monitoring's Edit saves it, and the audit names what changed. Every
 * Pushover PNMT sends follows it, so the rows say who sends at which importance.
 */
defineProps<{ canWrite: boolean }>()
const session = useSession()
const queryClient = useQueryClient()
const notify = useNotify()
const queryKey = [...keys.notifications, 'settings']
const query = useQuery({ queryKey, queryFn: api.notifications.settings })
const draft = ref<PushoverDraft | null>(null)
const dirty = computed(
  () =>
    !!query.data.value && !!draft.value && JSON.stringify(draftOf(query.data.value)) !== JSON.stringify(draft.value),
)
watch(
  () => query.data.value,
  (s) => {
    if (s && !dirty.value) draft.value = draftOf(s)
  },
  { immediate: true },
)
const serverErrors = ref<Record<string, string[]>>({})
const errors = computed<Partial<Record<keyof PushoverDraft, string>>>(() => ({
  ...(draft.value ? pushoverErrors(draft.value) : {}),
  ...Object.fromEntries(Object.entries(serverErrors.value).map(([k, v]) => [k, v.join(' ')])),
}))
const emergency = computed(() => !!draft.value && usesEmergency(draft.value))
const busy = ref(false)

async function save() {
  if (!draft.value) return
  busy.value = true
  serverErrors.value = {}
  try {
    const saved = await api.notifications.updateSettings(requestOf(draft.value))
    queryClient.setQueryData(queryKey, saved)
    draft.value = draftOf(saved)
    notify.success('Saved')
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      serverErrors.value = e.errors
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    busy.value = false
  }
}

function reset() {
  serverErrors.value = {}
  draft.value = query.data.value ? draftOf(query.data.value) : null
}
</script>

<template>
  <ErrorState
    v-if="query.isError.value && !draft"
    :error="query.error.value"
    what="the Pushover priorities"
    @retry="query.refetch()"
  />
  <section v-else-if="draft" class="panel" aria-labelledby="pushover-title">
    <div id="pushover-title" class="panel-header">Pushover priorities</div>
    <div class="panel-body form">
      <p class="muted intro">
        What a message of each importance is on the phone. Every Pushover PNMT sends follows this: the notification
        rules', Cookie's, the access watch's and MCP's strike notices. Readers see it; Monitoring: Edit changes it.
      </p>
      <div class="wide-table">
        <table class="rows">
          <thead>
            <tr>
              <th scope="col">Importance</th>
              <th scope="col">Priority</th>
              <th scope="col">Sent at it</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="row in importanceRows" :key="row.key">
              <th scope="row">{{ row.label }}</th>
              <td>
                <Select
                  v-model="draft[row.key]"
                  :options="pushoverPriorities"
                  option-label="label"
                  option-value="value"
                  :disabled="!canWrite"
                  :aria-label="`${row.label}: the Pushover priority`"
                  class="priority"
                >
                  <template #option="{ option }">
                    <span>{{ option.label }}</span>
                    <span class="muted small hint">{{ option.hint }}</span>
                  </template>
                </Select>
              </td>
              <td class="muted small">{{ row.hint }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <template v-if="emergency">
        <p class="muted small">
          An emergency repeats on the phone until the person acknowledges it or the expiry passes; PNMT doesn't read the
          acknowledgement.
        </p>
        <div class="pair">
          <div class="field">
            <label for="pushover-retry">Repeat every (seconds)</label>
            <InputNumber
              v-model="draft.emergencyRetrySeconds"
              input-id="pushover-retry"
              :min="MIN_RETRY"
              :max="MAX_RETRY"
              :disabled="!canWrite"
              :invalid="!!errors.emergencyRetrySeconds"
            />
            <small v-if="errors.emergencyRetrySeconds" class="field-error">{{ errors.emergencyRetrySeconds }}</small>
            <small v-else class="field-hint">{{ MIN_RETRY }} to {{ MAX_RETRY }}.</small>
          </div>
          <div class="field">
            <label for="pushover-expire">Give up after (seconds)</label>
            <InputNumber
              v-model="draft.emergencyExpireSeconds"
              input-id="pushover-expire"
              :min="MIN_RETRY"
              :max="MAX_EXPIRE"
              :disabled="!canWrite"
              :invalid="!!errors.emergencyExpireSeconds"
            />
            <small v-if="errors.emergencyExpireSeconds" class="field-error">{{ errors.emergencyExpireSeconds }}</small>
            <small v-else class="field-hint">At least the repeat, at most {{ MAX_EXPIRE }} (three hours).</small>
          </div>
        </div>
      </template>
      <div class="actions">
        <template v-if="canWrite">
          <Button
            label="Save"
            :disabled="!dirty || Object.keys(errors).length > 0 || busy"
            :loading="busy"
            @click="save"
          />
          <Button label="Reset" severity="secondary" text :disabled="!dirty || busy" @click="reset" />
        </template>
        <span v-if="query.data.value?.changedAt" class="muted small">
          Changed <RelativeTime :value="query.data.value.changedAt" /> by {{ query.data.value.changedBy }}
        </span>
        <span v-else class="muted small">Never changed: the defaults.</span>
        <RouterLink v-if="session.can('System', 'Manage')" :to="{ name: 'debug' }" class="small"
          >Send a test Pushover ›</RouterLink
        >
      </div>
    </div>
  </section>
</template>

<style scoped>
.intro {
  margin: 0;
}

.priority {
  min-width: 14rem;
}

.hint {
  margin-left: var(--pnmt-space-2);
}

.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
