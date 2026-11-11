<script setup lang="ts">
import Button from 'primevue/button'
import InputOtp from 'primevue/inputotp'
import Password from 'primevue/password'
import { useConfirm } from 'primevue/useconfirm'
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { authApi } from '@/api/auth'
import { ApiError } from '@/api/client'
import PageHeader from '@/components/PageHeader.vue'
import { useNotify } from '@/composables/useNotify'
import { useSession } from '@/stores/session'

/** Two-factor authentication and sessions (§4.6, §4.7, §5.6). */
const session = useSession()
const router = useRouter()
const notify = useNotify()
const confirm = useConfirm()

const disabling = ref(false)
const password = ref('')
const code = ref('')
const errors = ref<{ password?: string; code?: string }>({})
const busy = ref(false)

function setUp() {
  void router.push({ name: 'setup-2fa', query: { returnUrl: '/account/security' } })
}

async function disable() {
  errors.value = {
    password: password.value ? undefined : 'Enter your password.',
    code: code.value.length === 6 ? undefined : 'Enter the 6-digit code.',
  }
  if (errors.value.password || errors.value.code) {
    return
  }
  busy.value = true
  try {
    await authApi.totpDisable(password.value, code.value)
    await session.load(true)
    disabling.value = false
    password.value = code.value = ''
    notify.success('Two-factor authentication is off', 'Your other sessions were signed out.')
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = { password: e.field('password'), code: e.field('code') }
      code.value = ''
    } else {
      notify.error(e, 'Not turned off')
    }
  } finally {
    busy.value = false
  }
}

function revokeSessions() {
  confirm.require({
    header: 'Sign out everywhere else?',
    message: 'Every other browser or device signed in to your account will have to sign in again. This one stays.',
    icon: 'pi pi-sign-out',
    acceptProps: { label: 'Sign out other sessions', severity: 'danger' },
    rejectProps: { label: 'Cancel', severity: 'secondary', text: true },
    accept: async () => {
      try {
        await authApi.revokeSessions()
        notify.success('Other sessions signed out')
      } catch (e) {
        notify.error(e, 'Not signed out')
      }
    },
  })
}
</script>

<template>
  <div class="page">
    <PageHeader title="Security" />
    <div class="two-columns">
      <section class="panel" aria-labelledby="totp-title">
        <div id="totp-title" class="panel-header">
          Two-factor authentication
          <span v-if="session.user?.isTotpEnabled" class="state on"><i class="pi pi-shield" /> On</span>
          <span v-else class="state off">Off</span>
        </div>
        <div class="panel-body form narrow">
          <template v-if="!session.user?.isTotpEnabled">
            <p class="text">
              Signing in then needs a code from an authenticator app on your phone as well as your password. There are
              no recovery codes: if you lose the phone, an administrator turns it off for you.
            </p>
            <div><Button label="Set up" icon="pi pi-shield" @click="setUp" /></div>
          </template>
          <template v-else-if="!disabling">
            <p class="text">Signing in needs your password and a code from your authenticator app.</p>
            <div>
              <Button label="Turn off…" severity="secondary" outlined @click="disabling = true" />
            </div>
          </template>
          <form v-else class="form" novalidate @submit.prevent="disable">
            <p class="text">To turn it off, confirm with your password and a current code.</p>
            <input type="text" name="username" autocomplete="username" hidden />
            <div class="field">
              <label for="disable-password">Password</label>
              <Password
                v-model="password"
                input-id="disable-password"
                :feedback="false"
                toggle-mask
                fluid
                :invalid="!!errors.password"
                :input-props="{ autocomplete: 'current-password' }"
              />
              <small v-if="errors.password" class="field-error">{{ errors.password }}</small>
            </div>
            <div class="field">
              <label id="disable-code-label">Code</label>
              <InputOtp
                v-model="code"
                :length="6"
                integer-only
                aria-labelledby="disable-code-label"
                :invalid="!!errors.code"
              />
              <small v-if="errors.code" class="field-error">{{ errors.code }}</small>
            </div>
            <div class="form-actions">
              <Button type="submit" label="Turn off" severity="danger" :loading="busy" />
              <Button label="Cancel" severity="secondary" text @click="disabling = false" />
            </div>
          </form>
        </div>
      </section>
      <section class="panel" aria-labelledby="sessions-title">
        <div id="sessions-title" class="panel-header">Sessions</div>
        <div class="panel-body form narrow">
          <p class="text">
            A session lasts 7 days from its last use. Changing your password or two-factor authentication also signs out
            your other sessions.
          </p>
          <div>
            <Button
              label="Sign out other sessions"
              icon="pi pi-sign-out"
              severity="secondary"
              outlined
              @click="revokeSessions"
            />
          </div>
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.text {
  margin: 0;
}

.state {
  margin-left: auto;
  font-weight: 500;
}

.on {
  color: var(--pnmt-ok);
}

.off {
  color: var(--pnmt-muted);
}

.narrow {
  max-width: 460px;
}
</style>
