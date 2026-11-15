<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputOtp from 'primevue/inputotp'
import Message from 'primevue/message'
import { ref, watch } from 'vue'
import { authApi } from '@/api/auth'
import { ApiError } from '@/api/client'
import { queryClient } from '@/api/queries'
import { useDailyTotp } from '@/composables/useDailyTotp'
import { useSession } from '@/stores/session'

/**
 * The day's code (docs/mcp-plan.md §3.3), over whatever page is open: PNMT asks for it once a day, at 04:00, without
 * signing anyone out. A wrong code counts towards the same lockout as at sign-in.
 */
const daily = useDailyTotp()
const session = useSession()
const code = ref('')
const busy = ref(false)
const error = ref<string | null>(null)

watch(daily.open, (open) => {
  if (open) {
    code.value = ''
    error.value = null
  }
})

async function submit() {
  if (code.value.length !== 6) return
  busy.value = true
  error.value = null
  try {
    await authApi.totpDaily(code.value)
    daily.close()
    await session.load(true)
    await queryClient.invalidateQueries()
  } catch (e) {
    code.value = ''
    error.value =
      e instanceof ApiError && e.code === 'locked_out'
        ? 'Too many wrong codes. Try again later.'
        : e instanceof ApiError && e.code === 'invalid_code'
          ? 'Wrong code, or one that was used already.'
          : e instanceof Error
            ? e.message
            : 'The code could not be checked.'
  } finally {
    busy.value = false
  }
}

async function signOut() {
  daily.close()
  await session.logout()
  location.assign(`${import.meta.env.BASE_URL}login`)
}
</script>

<template>
  <Dialog
    :visible="daily.open.value"
    modal
    :closable="false"
    header="Your 2FA code for today"
    :style="{ width: '24rem' }"
    @update:visible="(v: boolean) => !v && daily.close()"
  >
    <form class="form" novalidate @submit.prevent="submit">
      <p class="muted">
        PNMT asks for the code once a day. Enter the code your authenticator app shows; the page stays as it is.
      </p>
      <Message v-if="error" severity="error" :closable="false" role="alert">{{ error }}</Message>
      <InputOtp v-model="code" :length="6" integer-only aria-label="Code" :disabled="busy" autofocus />
      <div class="actions">
        <Button label="Sign out" text severity="secondary" type="button" @click="signOut" />
        <Button type="submit" label="Continue" :loading="busy" :disabled="code.length !== 6" />
      </div>
    </form>
  </Dialog>
</template>

<style scoped>
.form {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.actions {
  display: flex;
  justify-content: space-between;
}
</style>
