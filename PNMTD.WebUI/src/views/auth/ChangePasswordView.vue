<script setup lang="ts">
import Button from 'primevue/button'
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { nextAfterSignIn, safeReturnUrl } from '@/router/guard'
import { useSession } from '@/stores/session'
import PasswordForm from '../account/PasswordForm.vue'

/**
 * A temporary password, or the first admin's from init.sh (§4.4, §4.8): it must be changed before
 * anything else. The server answers every other call with 403 must_change_password until then.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const returnUrl = computed(() => safeReturnUrl(route.query.returnUrl))

async function changed() {
  await session.load(true)
  await router.replace(nextAfterSignIn(session.flags!, returnUrl.value))
}

async function signOut() {
  await session.logout()
  await router.replace({ name: 'login' })
}
</script>

<template>
  <div class="form">
    <h1>Choose a new password</h1>
    <p class="muted intro">
      {{ session.user?.displayName }}, your password was set for you. Choose one of your own before you continue.
    </p>
    <PasswordForm current-label="Password you signed in with" submit-label="Save and continue" @changed="changed">
      <template #actions>
        <Button label="Sign out" text severity="secondary" @click="signOut" />
      </template>
    </PasswordForm>
  </div>
</template>

<style scoped>
.intro {
  margin: 0;
}
</style>
