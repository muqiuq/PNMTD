<script setup lang="ts">
import LogoMark from '@/components/LogoMark.vue'
import { useSession } from '@/stores/session'

/** The sign-in screens (§5.6): a centred card with the logo and the instance's identity. */
const session = useSession()
</script>

<template>
  <div class="auth-layout">
    <main class="auth-card panel">
      <header class="brand">
        <LogoMark :size="34" />
        <div>
          <div class="wordmark">PNMT</div>
          <div v-if="session.identity" class="identity muted">{{ session.identity }}</div>
        </div>
      </header>
      <slot />
    </main>
    <!-- The version is for signed-in users only (/system/info). -->
    <footer v-if="session.info?.version" class="muted">PNMTD {{ session.info.version }}</footer>
  </div>
</template>

<style scoped>
.auth-layout {
  min-height: 100vh;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--pnmt-space-4);
  padding: var(--pnmt-space-4);
}

.auth-card {
  width: 100%;
  max-width: 400px;
  padding: var(--pnmt-space-6);
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-4);
}

.brand {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
  color: var(--p-primary-color);
}

.wordmark {
  font-size: 1.5rem;
  font-weight: 500;
  letter-spacing: 0.06em;
  line-height: 1.1;
}

.identity {
  font-size: 0.9286rem;
}

footer {
  font-size: 0.857rem;
}

@media (max-width: 599px) {
  .auth-card {
    padding: var(--pnmt-space-4);
    border: none;
    background: transparent;
  }
}
</style>
