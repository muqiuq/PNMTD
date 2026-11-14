<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { useNotify } from '@/composables/useNotify'
import PlanFields from './availability/PlanFields.vue'
import { planDraftOf, planRequest, type PlanDraft } from './availability/plan'
import { useTypeOptions } from './availability/useAvailability'

/**
 * A person's contact details and their place in the availability plan (docs/cookie-plan.md §8.1), for admins: the e-mail
 * address and Pushover key (each person sets their own on Account too), the Redmine user, which only admins set (it
 * decides whose tickets' reminders a person gets), and the plan's fields.
 */
const props = defineProps<{ userId: string; username: string }>()
const queryClient = useQueryClient()
const notify = useNotify()
const profile = useQuery({
  queryKey: computed(() => [...keys.users, props.userId, 'profile']),
  queryFn: () => api.users.profile(props.userId),
})
const { types } = useTypeOptions()
const contact = ref({ email: '', pushoverKey: '', redmineName: '' })
const plan = ref<PlanDraft | null>(null)
const errors = ref<Record<string, string | undefined>>({})
const busy = ref(false)

watch(
  () => profile.data.value,
  (p) => {
    if (!p) return
    contact.value = { email: p.email ?? '', pushoverKey: p.pushoverKey ?? '', redmineName: p.redmineName ?? '' }
    plan.value = planDraftOf(p)
    errors.value = {}
  },
  { immediate: true },
)
const orNull = (v: string) => (v.trim() ? v.trim() : null)

async function save() {
  if (!plan.value) return
  busy.value = true
  errors.value = {}
  try {
    const saved = await api.users.saveProfile(props.userId, {
      email: orNull(contact.value.email),
      pushoverKey: orNull(contact.value.pushoverKey),
      redmineName: orNull(contact.value.redmineName),
      ...planRequest(plan.value),
    })
    queryClient.setQueryData([...keys.users, props.userId, 'profile'], saved)
    notify.success(`${props.username} saved`, saved.inPlan ? `In the plan as ${saved.shortCode}.` : undefined)
    await queryClient.invalidateQueries({ queryKey: keys.availability })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = Object.fromEntries(Object.entries(e.errors).map(([k, v]) => [k, v[0]]))
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <ErrorState
    v-if="profile.isError.value && !profile.data.value"
    :error="profile.error.value"
    what="the contact details"
    @retry="profile.refetch()"
  />
  <div v-else-if="!plan" class="center"><ProgressSpinner style="width: 24px; height: 24px" /></div>
  <form v-else class="form" novalidate @submit.prevent="save">
    <div class="field">
      <label for="up-email">E-mail address</label>
      <InputText id="up-email" v-model="contact.email" type="email" :invalid="!!errors.email" />
      <small v-if="errors.email" class="field-error">{{ errors.email }}</small>
    </div>
    <div class="field">
      <label for="up-pushover">Pushover user key</label>
      <InputText
        id="up-pushover"
        v-model="contact.pushoverKey"
        class="mono"
        maxlength="30"
        autocomplete="off"
        spellcheck="false"
        :invalid="!!errors.pushoverKey"
      />
      <small v-if="errors.pushoverKey" class="field-error">{{ errors.pushoverKey }}</small>
    </div>
    <div class="field">
      <label for="up-redmine">Redmine user</label>
      <InputText id="up-redmine" v-model="contact.redmineName" maxlength="200" :invalid="!!errors.redmineName" />
      <small v-if="errors.redmineName" class="field-error">{{ errors.redmineName }}</small>
      <small v-else class="field-hint"
        >The name Redmine shows on tickets: how an assignee is found, and whose reminders they get.</small
      >
    </div>
    <PlanFields v-model="plan" :types="types" :errors="errors" id-prefix="up" />
    <div class="form-actions"><Button type="submit" label="Save" size="small" :loading="busy" /></div>
  </form>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-3);
}
</style>
