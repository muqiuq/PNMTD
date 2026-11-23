<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import ProgressSpinner from 'primevue/progressspinner'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { useNotify } from '@/composables/useNotify'
import { useSession } from '@/stores/session'
import { serverAccountsFromText } from '@/views/cookie/access'
import PlanFields from './availability/PlanFields.vue'
import { planDraftOf, planRequest, type PlanDraft } from './availability/plan'
import { useTypeOptions } from './availability/useAvailability'
import { useCookieSettings } from './cookie/useSection'

/**
 * A person's contact details and their place in the availability plan (docs/cookie-plan.md §8.1), for admins: the e-mail
 * address and Pushover key (each person sets their own on Account too), the Redmine user, which only admins set (it
 * decides whose tickets' reminders a person gets), and the plan's fields. And the person's accounts on servers and routers,
 * which link a login to them for the access watch (docs/cookie-access-plan.md §5.3), while that way is on under Admin →
 * Cookie → Access; hidden, they are left as they are.
 */
const props = defineProps<{ userId: string; username: string }>()
const queryClient = useQueryClient()
const notify = useNotify()
const profile = useQuery({
  queryKey: computed(() => [...keys.users, props.userId, 'profile']),
  queryFn: () => api.users.profile(props.userId),
})
const { types } = useTypeOptions()
const session = useSession()
const cookieSettings = session.can('Cookie', 'Manage') ? useCookieSettings() : null
const accountsShown = computed(() => cookieSettings?.data.value?.access.links?.serverAccounts !== false)
const contact = ref({ email: '', pushoverKey: '', redmineName: '', serverAccounts: '' })
const plan = ref<PlanDraft | null>(null)
const errors = ref<Record<string, string | undefined>>({})
const busy = ref(false)

watch(
  () => profile.data.value,
  (p) => {
    if (!p) return
    contact.value = {
      email: p.email ?? '',
      pushoverKey: p.pushoverKey ?? '',
      redmineName: p.redmineName ?? '',
      serverAccounts: (p.serverAccounts ?? []).join('\n'),
    }
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
      serverAccounts: accountsShown.value ? serverAccountsFromText(contact.value.serverAccounts) : null,
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
      <small v-else-if="profile.data.value?.redmineUserId != null" class="field-hint"
        >Linked to Redmine user #{{ profile.data.value.redmineUserId }} on Admin → Cookie → Redmine: their tickets'
        reminders reach this person, whatever the name. Changing the name here removes the link.</small
      >
      <small v-else class="field-hint"
        >The name Redmine shows on tickets: how an assignee is found, and whose reminders they get. Not linked: someone
        with Redmine: Manage links it on Admin → Cookie → Redmine.</small
      >
    </div>
    <div v-if="accountsShown" class="field">
      <label for="up-server-accounts">Server accounts</label>
      <Textarea
        id="up-server-accounts"
        v-model="contact.serverAccounts"
        rows="2"
        auto-resize
        class="mono"
        spellcheck="false"
        placeholder="CONTOSO\jdoe"
        :invalid="!!errors.serverAccounts"
      />
      <small v-if="errors.serverAccounts" class="field-error">{{ errors.serverAccounts }}</small>
      <small v-else class="field-hint"
        >One per line, at most 20: the person's accounts on servers and routers (CONTOSO\jdoe, jdoe@contoso.local,
        jdoe). The access watch links a login to them by these, without case, with or without the domain.</small
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
