<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, ref } from 'vue'
import { useRoute } from 'vue-router'
import { ApiError } from '@/api/client'
import { api } from '@/api/resources'
import { formatAbsolute } from '@/format'

/**
 * Was this you? (docs/cookie-access-plan.md §6.6, §9): the page the e-mail's link opens, without sign-in, in the sign-in
 * screens' layout. It shows the one login the link asks about; opening it answers nothing (mail scanners open links), only
 * its two buttons do. A link used, expired or unknown shows the same: it no longer works.
 */
const route = useRoute()
const token = computed(() => String(route.params.token ?? ''))
const view = useQuery({
  queryKey: computed(() => ['was-this-you', token.value]),
  queryFn: () => api.wasThisYou.view(token.value),
  retry: false,
  refetchOnWindowFocus: false,
  staleTime: Infinity,
})
const v = computed(() => view.data.value)
const busy = ref(false)
const message = ref<string | null>(null)
/** The answer came too late: used, expired, or unknown (409). */
const gone = ref(false)
const error = ref<string | null>(null)

async function answer(itWasMe: boolean) {
  busy.value = true
  error.value = null
  try {
    message.value = (await api.wasThisYou.answer(token.value, itWasMe)).message
  } catch (e) {
    if (e instanceof ApiError && e.status === 409) gone.value = true
    else
      error.value =
        e instanceof ApiError && e.status === 429
          ? 'Too many tries. Wait a minute, then answer again.'
          : 'The answer was not sent. Try again.'
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="form">
    <h1>Was this you?</h1>
    <div v-if="view.isPending.value" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <Message v-else-if="view.isError.value" severity="error" :closable="false" role="alert">
      The login could not be loaded. Try again later.
    </Message>
    <template v-else-if="!v?.open || gone">
      <Message severity="warn" :closable="false">This link no longer works.</Message>
      <p class="muted">
        Each link works once, for a limited time. If a login of yours worries you, tell the people who look after PNMT.
      </p>
    </template>
    <template v-else-if="message">
      <Message severity="success" :closable="false" role="status">{{ message }}</Message>
    </template>
    <template v-else>
      <p class="intro">PNMT saw this login, and it is new for your account:</p>
      <p class="login">{{ v.login }}</p>
      <dl class="facts">
        <template v-if="v.target">
          <dt>To</dt>
          <dd>{{ v.target }}</dd>
        </template>
        <template v-if="v.way">
          <dt>How</dt>
          <dd>{{ v.way }}</dd>
        </template>
        <template v-if="v.source">
          <dt>From</dt>
          <dd class="mono">{{ v.source }}</dd>
        </template>
        <template v-if="v.device">
          <dt>Device</dt>
          <dd>{{ v.device }}</dd>
        </template>
        <template v-if="v.at">
          <dt>When</dt>
          <dd>{{ formatAbsolute(v.at) }}</dd>
        </template>
      </dl>
      <Message v-if="error" severity="error" :closable="false" role="alert">{{ error }}</Message>
      <div class="buttons">
        <Button label="Yes, it was me" icon="pi pi-check" :loading="busy" fluid @click="answer(true)" />
        <Button
          label="No, it wasn't me"
          icon="pi pi-times"
          severity="danger"
          outlined
          :disabled="busy"
          fluid
          @click="answer(false)"
        />
      </div>
      <p class="muted small">
        If it wasn't you, the people who look after access are told at once. Nothing is answered until you choose.
      </p>
    </template>
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-3);
}

.intro,
.login {
  margin: 0;
}

.login {
  font-weight: 500;
  word-break: break-word;
}

.buttons {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.small {
  font-size: 0.857rem;
}
</style>
