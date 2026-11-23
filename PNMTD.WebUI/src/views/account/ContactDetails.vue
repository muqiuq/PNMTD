<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { useNotify } from '@/composables/useNotify'
import CodeChip from '@/views/admin/availability/CodeChip.vue'
import { contactKey, useContact } from '@/views/admin/availability/useAvailability'

/**
 * One's own contact details (docs/cookie-plan.md §8.1): the e-mail address and the Pushover user key, where PNMT's
 * assistant reaches one, each with Send a test. The Redmine user is set by admins (it decides whose tickets' reminders
 * one gets), the short code and colour by whoever manages the availability plan.
 */
const queryClient = useQueryClient()
const notify = useNotify()
const contact = useContact()
const c = computed(() => contact.data.value)
const form = ref({ email: '', pushoverKey: '' })
const base = ref('')
const busy = ref(false)
const errors = ref<Record<string, string | undefined>>({})
type Channel = 'email' | 'pushover'
const tests = ref<Partial<Record<Channel, { ok: boolean; detail: string }>>>({})
const testing = ref<Channel | null>(null)

watch(
  c,
  (value) => {
    if (value && (!base.value || JSON.stringify(form.value) === base.value)) {
      form.value = { email: value.email ?? '', pushoverKey: value.pushoverKey ?? '' }
      base.value = JSON.stringify(form.value)
    }
  },
  { immediate: true },
)
const dirty = computed(() => !!base.value && JSON.stringify(form.value) !== base.value)
const orNull = (v: string) => (v.trim() ? v.trim() : null)

async function save() {
  busy.value = true
  errors.value = {}
  try {
    const saved = await api.account.saveContact({
      email: orNull(form.value.email),
      pushoverKey: orNull(form.value.pushoverKey),
    })
    queryClient.setQueryData(contactKey, saved)
    // Whether there is an address decides what the notification choices offer (docs/knowledge-base-extensions-plan.md §10.2).
    await queryClient.invalidateQueries({ queryKey: [...keys.account, 'notifications'] })
    form.value = { email: saved.email ?? '', pushoverKey: saved.pushoverKey ?? '' }
    base.value = JSON.stringify(form.value)
    tests.value = {}
    notify.success('Contact details saved')
    await queryClient.invalidateQueries({ queryKey: keys.availability })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = { email: e.field('email'), pushoverKey: e.field('pushoverKey') }
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    busy.value = false
  }
}

/** A test to the saved address or key; the server says what came of it (409: none saved, or no provider). */
async function test(channel: Channel) {
  testing.value = channel
  try {
    tests.value = { ...tests.value, [channel]: await api.account.testContact(channel) }
  } catch (e) {
    if (e instanceof ApiError && e.status === 409) {
      tests.value = { ...tests.value, [channel]: { ok: false, detail: e.message } }
    } else {
      notify.error(e, 'No test sent')
    }
  } finally {
    testing.value = null
  }
}
</script>

<template>
  <section class="panel" aria-labelledby="contact-title">
    <div id="contact-title" class="panel-header">Contact details</div>
    <ErrorState
      v-if="contact.isError.value && !c"
      :error="contact.error.value"
      what="your contact details"
      @retry="contact.refetch()"
    />
    <form v-else class="panel-body form" novalidate @submit.prevent="save">
      <p class="muted small intro">
        Where PNMT's assistant reaches you: important messages by e-mail, urgent ones by Pushover too, and only in
        working hours. The notification rules keep their own recipients.
      </p>
      <div class="field">
        <label for="contact-email">E-mail address</label>
        <span class="with-test">
          <InputText
            id="contact-email"
            v-model="form.email"
            type="email"
            autocomplete="email"
            class="grow"
            :invalid="!!errors.email"
          />
          <Button
            label="Send a test"
            icon="pi pi-send"
            size="small"
            severity="secondary"
            outlined
            :loading="testing === 'email'"
            :disabled="dirty || !c?.email"
            aria-label="Send a test e-mail"
            @click="test('email')"
          />
        </span>
        <small v-if="errors.email" class="field-error">{{ errors.email }}</small>
        <Message v-if="tests.email" :severity="tests.email.ok ? 'success' : 'error'" size="small" :closable="false">
          {{ tests.email.ok ? 'Sent' : 'Not sent' }}: {{ tests.email.detail }}
        </Message>
      </div>
      <div class="field">
        <label for="contact-pushover">Pushover user key</label>
        <span class="with-test">
          <InputText
            id="contact-pushover"
            v-model="form.pushoverKey"
            class="grow mono"
            maxlength="30"
            autocomplete="off"
            spellcheck="false"
            :invalid="!!errors.pushoverKey"
          />
          <Button
            label="Send a test"
            icon="pi pi-send"
            size="small"
            severity="secondary"
            outlined
            :loading="testing === 'pushover'"
            :disabled="dirty || !c?.pushoverKey"
            aria-label="Send a test by Pushover"
            @click="test('pushover')"
          />
        </span>
        <small v-if="errors.pushoverKey" class="field-error">{{ errors.pushoverKey }}</small>
        <small v-else class="field-hint">30 letters and digits, from the Pushover app or pushover.net.</small>
        <Message
          v-if="tests.pushover"
          :severity="tests.pushover.ok ? 'success' : 'error'"
          size="small"
          :closable="false"
        >
          {{ tests.pushover.ok ? 'Sent' : 'Not sent' }}: {{ tests.pushover.detail }}
        </Message>
      </div>
      <div class="form-actions">
        <Button type="submit" label="Save" size="small" :loading="busy" :disabled="!dirty" />
        <span v-if="dirty" class="muted small">Save first, then send a test.</span>
      </div>
      <dl class="facts">
        <dt>Redmine user</dt>
        <dd>
          <template v-if="c?.redmineName"
            >{{ c.redmineName
            }}<template v-if="c.redmineUserId != null"> (#{{ c.redmineUserId }}), <strong>linked</strong></template
            ><span v-else class="muted">, not linked</span></template
          >
          <span v-else class="muted">not set</span>
          <div class="muted small">
            Whose tickets' reminders you get. Linked by someone with Redmine: Manage; until then, found by the name.
          </div>
        </dd>
        <dt>Availability plan</dt>
        <dd>
          <template v-if="c?.inPlan">
            <CodeChip :code="c.shortCode" :colour="c.color" />
            ·
            <RouterLink :to="{ name: 'availability' }">Your column ›</RouterLink>
          </template>
          <span v-else class="muted">not in the plan</span>
        </dd>
      </dl>
    </form>
  </section>
</template>

<style scoped>
.intro {
  margin: 0;
}

.with-test {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.grow {
  flex: 1;
  min-width: 0;
}

.facts {
  margin-top: var(--pnmt-space-2);
  padding-top: var(--pnmt-space-3);
  border-top: 1px solid var(--pnmt-border);
}

.small {
  font-size: 0.857rem;
}
</style>
