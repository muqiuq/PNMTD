<script setup lang="ts">
import Button from 'primevue/button'
import InputOtp from 'primevue/inputotp'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { authApi, type TotpSetup } from '@/api/auth'
import { ApiError } from '@/api/client'
import CopyField from '@/components/CopyField.vue'
import QrCode from '@/components/QrCode.vue'
import { useNotify } from '@/composables/useNotify'
import { safeReturnUrl } from '@/router/guard'
import { useSession } from '@/stores/session'

/**
 * Setting up 2FA (§4.6): the prompt after a sign-in without it (Optional policy, "Later" lasts
 * until the next sign-in), or the required setup (RequiredForAdmins, Required). The secret is
 * pending on the server until a code from it confirms that the app has it.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const notify = useNotify()

const required = computed(() => session.flags?.totpSetupRequired === true)
const returnUrl = computed(() => safeReturnUrl(route.query.returnUrl))
const stage = ref<'prompt' | 'setup'>(route.query.prompt === '1' && !required.value ? 'prompt' : 'setup')
const setup = ref<TotpSetup | null>(null)
const loading = ref(false)
const loadError = ref<string | null>(null)
const alreadyOn = ref(false)
const expired = ref(false)
const code = ref('')
const codeError = ref<string | null>(null)
const busy = ref(false)

const grouped = computed(() => setup.value?.secret.match(/.{1,4}/g)?.join(' ') ?? '')

async function start() {
  stage.value = 'setup'
  loading.value = true
  loadError.value = null
  expired.value = false
  code.value = ''
  codeError.value = null
  try {
    setup.value = await authApi.totpSetup()
    await nextTick()
    document.querySelector<HTMLInputElement>('.otp input')?.focus()
  } catch (e) {
    if (e instanceof ApiError && e.code === 'totp_already_enabled') {
      alreadyOn.value = true
    } else {
      loadError.value = e instanceof Error ? e.message : String(e)
    }
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  if (stage.value === 'setup') {
    void start()
  }
})

async function confirm() {
  if (code.value.length !== 6 || busy.value) {
    return
  }
  busy.value = true
  codeError.value = null
  try {
    await authApi.totpConfirm(code.value)
    await session.load(true)
    notify.success('Two-factor authentication is on', 'Your other sessions were signed out.')
    await router.replace(returnUrl.value ?? '/')
  } catch (e) {
    if (e instanceof ApiError && e.code === 'totp_setup_expired') {
      expired.value = true
    } else if (e instanceof ApiError && e.field('code')) {
      codeError.value = e.field('code')!
      code.value = ''
    } else {
      notify.error(e, 'Not turned on')
    }
  } finally {
    busy.value = false
  }
}

// The sixth digit confirms.
watch(code, (value) => {
  if (value?.length === 6) {
    void confirm()
  }
})

async function later() {
  await router.replace(returnUrl.value ?? '/')
}

async function signOut() {
  await session.logout()
  await router.replace({ name: 'login' })
}
</script>

<template>
  <div class="form">
    <template v-if="stage === 'prompt'">
      <h1>Protect your account with two-factor authentication</h1>
      <p class="muted text">
        With it, signing in needs a code from an app on your phone as well as your password, so a stolen password alone
        is not enough. It takes about a minute with any authenticator app (Aegis, Google or Microsoft Authenticator,
        Bitwarden, 1Password, …).
      </p>
      <div class="form-actions">
        <Button label="Set up now" icon="pi pi-shield" @click="start" />
        <Button label="Later" severity="secondary" text @click="later" />
      </div>
    </template>

    <template v-else>
      <h1>Set up two-factor authentication</h1>
      <Message v-if="required" severity="info" :closable="false">
        Two-factor authentication is required for your account. Set it up to continue.
      </Message>

      <div v-if="loading" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
      <Message v-else-if="loadError" severity="error" :closable="false">{{ loadError }}</Message>
      <template v-else-if="alreadyOn">
        <Message severity="success" :closable="false">Two-factor authentication is on already.</Message>
        <Button label="Continue" @click="later" />
      </template>
      <template v-else-if="expired">
        <Message severity="warn" :closable="false">The setup expired after 15 minutes. Start it again.</Message>
        <Button label="Start again" icon="pi pi-refresh" @click="start" />
      </template>

      <template v-else-if="setup">
        <ol class="steps">
          <li>
            <div>Scan this code with your authenticator app:</div>
            <QrCode :text="setup.otpauthUri" label="QR code for the authenticator app" class="qr" />
            <div class="muted">Or enter this key by hand (time-based):</div>
            <CopyField :value="setup.secret" :display="grouped" label="Setup key" />
          </li>
          <li>
            <label id="confirm-label">Enter the 6-digit code the app shows now:</label>
            <InputOtp
              v-model="code"
              class="otp"
              :length="6"
              integer-only
              aria-labelledby="confirm-label"
              :disabled="busy"
              :invalid="!!codeError"
            />
            <small v-if="codeError" class="field-error" role="alert">{{ codeError }}</small>
          </li>
        </ol>
        <div class="form-actions">
          <Button label="Turn on" icon="pi pi-check" :loading="busy" :disabled="code.length !== 6" @click="confirm" />
          <Button v-if="!required" label="Cancel" severity="secondary" text @click="later" />
        </div>
      </template>

      <Button v-if="required" label="Sign out" severity="secondary" text class="sign-out" @click="signOut" />
    </template>
  </div>
</template>

<style scoped>
.text {
  margin: 0;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}

.steps {
  margin: 0;
  padding-left: 1.25rem;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-4);
}

.steps li {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.qr {
  align-self: center;
}

.otp {
  justify-content: space-between;
}

.otp :deep(input) {
  width: 2.6rem;
  height: 2.6rem;
  font-size: 1.15rem;
  text-align: center;
  font-family: var(--pnmt-mono);
}

.sign-out {
  align-self: flex-start;
}
</style>
