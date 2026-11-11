<script setup lang="ts">
import Button from 'primevue/button'
import InputOtp from 'primevue/inputotp'
import Message from 'primevue/message'
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { authApi } from '@/api/auth'
import { ApiError } from '@/api/client'
import { nextAfterSignIn, safeReturnUrl } from '@/router/guard'
import { useSession } from '@/stores/session'
import { signInMessage } from './messages'

/** Step 2 (§4.5): the code from the authenticator app, within 5 minutes of the password. */
const route = useRoute()
const router = useRouter()
const session = useSession()

const code = ref('')
const busy = ref(false)
const error = ref<string | null>(null)
const returnUrl = computed(() => safeReturnUrl(route.query.returnUrl))

onMounted(async () => {
  await nextTick()
  document.querySelector<HTMLInputElement>('.otp input')?.focus()
})

async function submit() {
  if (code.value.length !== 6 || busy.value) {
    return
  }
  busy.value = true
  error.value = null
  try {
    const result = await authApi.loginTotp(code.value)
    session.set(result.user!, result.flags!)
    await session.loadInfo()
    await router.replace(nextAfterSignIn(result.flags!, returnUrl.value))
  } catch (e) {
    if (e instanceof ApiError && e.code === 'login_expired') {
      await router.replace({
        name: 'login',
        query: { expired: '1', ...(returnUrl.value ? { returnUrl: returnUrl.value } : {}) },
      })
      return
    }
    error.value = signInMessage(e)
    code.value = ''
    await nextTick()
    document.querySelector<HTMLInputElement>('.otp input')?.focus()
  } finally {
    busy.value = false
  }
}

// The sixth digit submits.
watch(code, (value) => {
  if (value?.length === 6) {
    void submit()
  }
})
</script>

<template>
  <form class="form" novalidate @submit.prevent="submit">
    <h1>Two-factor authentication</h1>
    <p class="muted intro">Enter the 6-digit code that your authenticator app shows for this account.</p>
    <Message v-if="error" severity="error" :closable="false" role="alert">{{ error }}</Message>
    <div class="field">
      <label id="code-label">Code</label>
      <InputOtp v-model="code" class="otp" :length="6" integer-only aria-labelledby="code-label" :disabled="busy" />
    </div>
    <Button type="submit" label="Verify" :loading="busy" :disabled="code.length !== 6" fluid />
    <RouterLink :to="{ name: 'login', query: returnUrl ? { returnUrl } : {} }" class="back"
      >Back to the password</RouterLink
    >
  </form>
</template>

<style scoped>
.intro {
  margin: 0;
}

.otp {
  justify-content: space-between;
}

.otp :deep(input) {
  width: 2.75rem;
  height: 2.75rem;
  font-size: 1.25rem;
  text-align: center;
  font-family: var(--pnmt-mono);
}

.back {
  text-align: center;
}
</style>
