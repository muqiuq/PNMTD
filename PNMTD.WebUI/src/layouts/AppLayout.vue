<script setup lang="ts">
import Button from 'primevue/button'
import Menu from 'primevue/menu'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import LogoMark from '@/components/LogoMark.vue'
import { useMedia } from '@/composables/useMedia'
import { usePreferences } from '@/stores/preferences'
import { useSession } from '@/stores/session'
import { visibleNavigation } from './navigation'

/**
 * The shell (§5.4): a drawer with the groups of today, an app bar of 48 px, the page. On a desktop
 * the drawer folds to an icon rail; below 960 px it is a rail; below 600 px an overlay that starts closed.
 */
const session = useSession()
const preferences = usePreferences()
const route = useRoute()
const router = useRouter()

const phone = useMedia('(max-width: 599px)')
const narrow = useMedia('(max-width: 959px)')
const overlayOpen = ref(false)

const groups = computed(() => visibleNavigation(session.can))
const rail = computed(() => !phone.value && (narrow.value || preferences.drawerCollapsed))
const drawerVisible = computed(() => !phone.value || overlayOpen.value)

watch(
  () => route.fullPath,
  () => (overlayOpen.value = false),
)

function toggleDrawer() {
  if (phone.value) {
    overlayOpen.value = !overlayOpen.value
  } else if (!narrow.value) {
    preferences.drawerCollapsed = !preferences.drawerCollapsed
  }
}

function isActive(name: string, to: string) {
  if (to === '/') {
    return route.name === 'dashboard'
  }
  return route.name === name || route.path.startsWith(`${to}/`) || route.path === to
}

const userMenu = ref<InstanceType<typeof Menu> | null>(null)
const userItems = computed(() => [
  {
    label: session.user?.displayName ?? '',
    items: [
      { label: 'Account', icon: 'pi pi-user', command: () => router.push({ name: 'account' }) },
      { label: 'Security', icon: 'pi pi-shield', command: () => router.push({ name: 'account-security' }) },
      { separator: true },
      { label: 'Sign out', icon: 'pi pi-sign-out', command: signOut },
    ],
  },
])

async function signOut() {
  await session.logout()
  await router.replace({ name: 'login' })
}
</script>

<template>
  <div class="app-layout" :class="{ rail, phone }">
    <a class="skip-link" href="#main">Skip to content</a>
    <div v-if="phone && overlayOpen" class="backdrop" @click="overlayOpen = false" />
    <aside v-show="drawerVisible" class="drawer" :class="{ overlay: phone }" aria-label="Navigation">
      <RouterLink to="/" class="drawer-brand" aria-label="PNMT dashboard">
        <LogoMark :size="26" />
        <span v-if="!rail" class="wordmark">PNMT</span>
      </RouterLink>
      <nav class="drawer-nav">
        <section v-for="group in groups" :key="group.label" class="group">
          <div v-if="!rail" class="group-label">{{ group.label }}</div>
          <div v-else class="group-divider" />
          <RouterLink
            v-for="item in group.items"
            :key="item.name"
            v-tooltip.right="rail ? item.label : undefined"
            :to="item.to"
            class="nav-item"
            :class="{ active: isActive(item.name, item.to) }"
            :aria-current="isActive(item.name, item.to) ? 'page' : undefined"
          >
            <i :class="item.icon" aria-hidden="true" />
            <span v-if="!rail" class="label">{{ item.label }}</span>
            <span v-else class="sr-only">{{ item.label }}</span>
          </RouterLink>
        </section>
      </nav>
      <footer v-if="!rail" class="drawer-footer">
        <div class="ellipsis" :title="session.identity">{{ session.identity }}</div>
        <div class="muted">PNMTD {{ session.info?.version ?? '' }}</div>
      </footer>
    </aside>

    <div class="main-column">
      <header class="app-bar">
        <Button
          icon="pi pi-bars"
          text
          rounded
          severity="secondary"
          :aria-label="phone ? 'Open navigation' : 'Fold navigation'"
          :aria-expanded="phone ? overlayOpen : !rail"
          @click="toggleDrawer"
        />
        <span class="instance ellipsis" :title="session.identity">{{ session.identity }}</span>
        <div class="app-bar-center"><slot name="search" /></div>
        <div class="app-bar-end">
          <slot name="status" />
          <Button
            v-tooltip.bottom="preferences.isDark() ? 'Light theme' : 'Dark theme'"
            :icon="preferences.isDark() ? 'pi pi-sun' : 'pi pi-moon'"
            text
            rounded
            severity="secondary"
            :aria-label="preferences.isDark() ? 'Light theme' : 'Dark theme'"
            @click="preferences.toggleTheme()"
          />
          <Button
            text
            severity="secondary"
            class="user-button"
            aria-haspopup="true"
            aria-controls="user-menu"
            :aria-label="`Account menu of ${session.user?.displayName}`"
            @click="(e: Event) => userMenu?.toggle(e)"
          >
            <span class="avatar" aria-hidden="true">{{
              (session.user?.displayName ?? '?').slice(0, 1).toUpperCase()
            }}</span>
            <span v-if="!phone" class="user-name ellipsis">{{ session.user?.displayName }}</span>
            <i class="pi pi-angle-down" aria-hidden="true" />
          </Button>
          <Menu id="user-menu" ref="userMenu" :model="userItems" popup />
        </div>
      </header>
      <main id="main" class="content" tabindex="-1">
        <slot />
      </main>
    </div>
  </div>
</template>

<style scoped>
.app-layout {
  display: flex;
  min-height: 100vh;
}

.skip-link {
  position: absolute;
  left: -999px;
  top: 0;
  z-index: 100;
  background: var(--pnmt-panel);
  padding: var(--pnmt-space-2);
}

.skip-link:focus {
  left: var(--pnmt-space-2);
}

.drawer {
  position: sticky;
  top: 0;
  height: 100vh;
  width: var(--pnmt-drawer-width);
  flex: 0 0 var(--pnmt-drawer-width);
  display: flex;
  flex-direction: column;
  background: var(--pnmt-panel);
  border-right: 1px solid var(--pnmt-border);
  z-index: 20;
}

.rail .drawer {
  width: var(--pnmt-rail-width);
  flex-basis: var(--pnmt-rail-width);
}

.drawer.overlay {
  position: fixed;
  left: 0;
  top: 0;
  box-shadow: var(--p-overlay-modal-shadow);
}

.backdrop {
  position: fixed;
  inset: 0;
  background: var(--p-mask-background);
  z-index: 19;
}

.drawer-brand {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  height: var(--pnmt-header-height);
  padding: 0 var(--pnmt-space-4);
  color: var(--p-primary-color);
  flex: 0 0 auto;
}

.rail .drawer-brand {
  padding: 0;
  justify-content: center;
}

.drawer-brand:hover {
  text-decoration: none;
}

.wordmark {
  font-size: 1.2rem;
  font-weight: 500;
  letter-spacing: 0.06em;
}

.drawer-nav {
  flex: 1 1 auto;
  overflow-y: auto;
  padding: 0 var(--pnmt-space-2) var(--pnmt-space-2);
}

.group-label {
  padding: var(--pnmt-space-3) var(--pnmt-space-2) var(--pnmt-space-1);
  font-size: 0.786rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--pnmt-muted);
}

.group-divider {
  height: 1px;
  background: var(--pnmt-border);
  margin: var(--pnmt-space-2) var(--pnmt-space-1);
}

.nav-item {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
  height: 34px;
  padding: 0 var(--pnmt-space-2);
  border-radius: var(--p-border-radius-md);
  color: var(--pnmt-text);
}

.rail .nav-item {
  justify-content: center;
  padding: 0;
}

.nav-item:hover {
  background: var(--p-content-hover-background);
  text-decoration: none;
}

.nav-item.active {
  background: var(--p-highlight-background);
  color: var(--p-highlight-color);
  font-weight: 500;
}

.nav-item i {
  font-size: 1rem;
  width: 1.25rem;
  text-align: center;
  color: var(--pnmt-muted);
}

.nav-item.active i {
  color: inherit;
}

.drawer-footer {
  padding: var(--pnmt-space-3) var(--pnmt-space-4);
  border-top: 1px solid var(--pnmt-border);
  font-size: 0.857rem;
}

.main-column {
  flex: 1 1 auto;
  min-width: 0;
  display: flex;
  flex-direction: column;
}

.app-bar {
  position: sticky;
  top: 0;
  z-index: 10;
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  height: var(--pnmt-header-height);
  padding: 0 var(--pnmt-space-2) 0 var(--pnmt-space-1);
  background: var(--pnmt-panel);
  border-bottom: 1px solid var(--pnmt-border);
}

.instance {
  font-weight: 500;
  max-width: 220px;
}

.app-bar-center {
  flex: 1 1 auto;
  display: flex;
  justify-content: center;
  min-width: 0;
}

.app-bar-end {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.user-button {
  gap: var(--pnmt-space-2);
}

.avatar {
  display: inline-grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border-radius: 50%;
  background: var(--p-primary-color);
  color: var(--p-primary-contrast-color);
  font-weight: 600;
  font-size: 0.857rem;
}

.user-name {
  max-width: 160px;
  color: var(--pnmt-text);
}

.content {
  flex: 1 1 auto;
  min-width: 0;
  outline: none;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}

@media (max-width: 599px) {
  .instance {
    max-width: 140px;
  }
}
</style>
