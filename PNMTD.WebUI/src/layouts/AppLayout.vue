<script setup lang="ts">
import Button from 'primevue/button'
import Menu from 'primevue/menu'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, ref, watch, watchEffect } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import GlobalSearch from '@/components/GlobalSearch.vue'
import LogoMark from '@/components/LogoMark.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useDashboard } from '@/composables/useDashboard'
import { useMedia } from '@/composables/useMedia'
import { api, keys } from '@/api/resources'
import { pageTitle } from '@/router'
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

// The failure count follows every page (§5.4): on the Dashboard item and in the tab title, so a tab
// in the background still shows problems.
const dashboard = useDashboard()
const failing = computed(() => dashboard.data.value?.summary.sensors.failing ?? 0)
// Open decisions (docs/ai-operations-plan.md §5.3), for those who may decide.
const decisions = useQuery({
  queryKey: [...keys.decisions, 'count'],
  queryFn: api.decisions.count,
  refetchInterval: 30_000,
  enabled: computed(() => session.can('User')),
})
const openDecisions = computed(() => decisions.data.value?.open ?? 0)
const updatedAt = computed(() =>
  dashboard.dataUpdatedAt.value ? new Date(dashboard.dataUpdatedAt.value).toISOString() : null,
)
watchEffect(() => {
  document.title = pageTitle(route.meta.title, session.identity, failing.value)
})

const queryClient = useQueryClient()
const refreshing = ref(false)
async function refresh() {
  refreshing.value = true
  try {
    await queryClient.refetchQueries({ type: 'active' })
  } finally {
    refreshing.value = false
  }
}

const groups = computed(() => visibleNavigation(session.can))
// An editing page holds the drawer as a rail while it is open (docs/locations-knowledge-plan.md §5.9);
// opening it then is for that page only, the stored preference stays as it was.
const openedWhileHeld = ref(false)
const rail = computed(
  () => !phone.value && (narrow.value || (preferences.railHeld ? !openedWhileHeld.value : preferences.drawerCollapsed)),
)
const drawerVisible = computed(() => !phone.value || overlayOpen.value)

watch(
  () => route.fullPath,
  () => (overlayOpen.value = false),
)
watch(
  () => preferences.railHeld,
  () => (openedWhileHeld.value = false),
)

function toggleDrawer() {
  if (phone.value) {
    overlayOpen.value = !overlayOpen.value
  } else if (narrow.value) {
    return
  } else if (preferences.railHeld) {
    openedWhileHeld.value = !openedWhileHeld.value
  } else {
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
      { label: 'Tunnels', icon: 'pi pi-lock', command: () => router.push({ name: 'account-tunnels' }) },
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
            <span
              v-if="item.name === 'dashboard' && failing"
              class="badge"
              :class="{ dot: rail }"
              :aria-label="`${failing} failing`"
              >{{ rail ? '' : failing }}</span
            >
            <span
              v-else-if="item.name === 'decisions' && openDecisions"
              class="badge"
              :class="{ dot: rail }"
              :aria-label="`${openDecisions} open`"
              >{{ rail ? '' : openDecisions }}</span
            >
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
        <div class="app-bar-center"><GlobalSearch v-if="!phone" /></div>
        <div class="app-bar-end">
          <span v-if="updatedAt && !phone" class="updated muted">
            {{ dashboard.isError.value ? 'Connection lost ·' : 'Updated' }} <RelativeTime :value="updatedAt" />
          </span>
          <Button
            v-tooltip.bottom="'Refresh'"
            icon="pi pi-refresh"
            text
            rounded
            severity="secondary"
            :loading="refreshing"
            aria-label="Refresh"
            @click="refresh"
          />
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
  position: relative;
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

.badge {
  margin-left: auto;
  min-width: 1.4rem;
  padding: 0 0.4rem;
  border-radius: 999px;
  background: var(--pnmt-fail);
  color: #fff;
  font-size: 0.786rem;
  font-weight: 600;
  text-align: center;
  line-height: 1.5;
}

.badge.dot {
  position: absolute;
  margin: 0 0 14px 18px;
  min-width: 0;
  width: 8px;
  height: 8px;
  padding: 0;
}

.updated {
  font-size: 0.857rem;
  white-space: nowrap;
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
