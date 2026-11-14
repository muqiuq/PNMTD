<script setup lang="ts">
import Tag from 'primevue/tag'
import PageHeader from '@/components/PageHeader.vue'
import { useSession } from '@/stores/session'
import MySshKeys from './MySshKeys.vue'
import PasswordForm from './PasswordForm.vue'

/** The own account (§5.6): who you are, your password, and your SSH keys (docs/linux-sensor-plan.md §6.4). */
const session = useSession()
</script>

<template>
  <div class="page">
    <PageHeader title="Account" />
    <div class="two-columns">
      <div class="column">
        <section class="panel" aria-labelledby="profile-title">
          <div id="profile-title" class="panel-header">Profile</div>
          <dl class="panel-body facts">
            <dt>Username</dt>
            <dd class="mono">{{ session.user?.username }}</dd>
            <dt>Name</dt>
            <dd>{{ session.user?.displayName }}</dd>
            <dt>Access</dt>
            <dd>
              <Tag v-if="session.isAdmin" value="Admin" severity="info" />
              <RouterLink v-else :to="{ name: 'account-security', hash: '#access-title' }"
                >Your groups and rights ›</RouterLink
              >
            </dd>
            <dt>Two-factor</dt>
            <dd>
              <span v-if="session.user?.isTotpEnabled" class="on"><i class="pi pi-shield" /> On</span>
              <span v-else class="muted">Off</span>
              · <RouterLink :to="{ name: 'account-security' }">Security</RouterLink>
            </dd>
          </dl>
        </section>
        <MySshKeys />
      </div>
      <section class="panel" aria-labelledby="password-title">
        <div id="password-title" class="panel-header">Change password</div>
        <div class="panel-body narrow">
          <PasswordForm />
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.on {
  color: var(--pnmt-ok);
}

.column {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.narrow {
  max-width: 420px;
}
</style>
