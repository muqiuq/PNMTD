<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Password from 'primevue/password'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { authApi } from '@/api/auth'
import { ApiError } from '@/api/client'
import { nextAfterSignIn, safeReturnUrl } from '@/router/guard'
import { useSession } from '@/stores/session'
import { signInMessage } from './messages'

/** Step 1 of the sign-in (§4.5): the password. With 2FA the code follows at /login/2fa. */
const route = useRoute()
const router = useRouter()
const session = useSession()

const username = ref('')
const password = ref('')
const busy = ref(false)
const error = ref<string | null>(route.query.expired ? 'The sign-in has expired. Enter your password again.' : null)
const returnUrl = computed(() => safeReturnUrl(route.query.returnUrl))

async function submit() {
  if (!username.value.trim() || !password.value) {
    error.value = 'Enter your username and password.'
    return
  }
  busy.value = true
  error.value = null
  try {
    const result = await authApi.login(username.value.trim(), password.value)
    if (result.next === 'totp') {
      await router.push({ name: 'login-2fa', query: returnUrl.value ? { returnUrl: returnUrl.value } : {} })
      return
    }
    session.set(result.user!, result.flags!)
    await session.loadInfo()
    await router.replace(nextAfterSignIn(result.flags!, returnUrl.value))
  } catch (e) {
    error.value = signInMessage(e)
    if (e instanceof ApiError && e.code === 'invalid_credentials') {
      password.value = ''
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <form class="form" novalidate @submit.prevent="submit">
    <h1>Sign in</h1>
    <Message v-if="error" severity="error" :closable="false" role="alert">{{ error }}</Message>
    <div class="field">
      <label for="username">Username</label>
      <InputText
        id="username"
        v-model="username"
        autocomplete="username"
        autofocus
        autocapitalize="none"
        spellcheck="false"
      />
    </div>
    <div class="field">
      <label for="password">Password</label>
      <Password
        v-model="password"
        input-id="password"
        :feedback="false"
        toggle-mask
        fluid
        :input-props="{ autocomplete: 'current-password' }"
      />
    </div>
    <Button type="submit" label="Sign in" :loading="busy" fluid />
  </form>
</template>
