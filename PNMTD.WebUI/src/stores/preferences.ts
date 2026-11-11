import { defineStore } from 'pinia'
import { ref, watch } from 'vue'
import { darkClass } from '@/design/preset'

export type Theme = 'system' | 'light' | 'dark'
export type HostsView = 'rows' | 'tiles'

const storageKey = 'pnmt.preferences'

interface Stored {
  theme?: Theme
  drawerCollapsed?: boolean
  hostsView?: HostsView
}

function read(): Stored {
  try {
    return JSON.parse(localStorage.getItem(storageKey) ?? '{}') as Stored
  } catch {
    return {}
  }
}

/** Per-browser preferences (§5.3, §5.5): theme, the drawer as a rail, the dashboard's host view. */
export const usePreferences = defineStore('preferences', () => {
  const stored = read()
  const theme = ref<Theme>(stored.theme ?? 'system')
  const drawerCollapsed = ref(stored.drawerCollapsed ?? false)
  // null: the default, rows on a desktop and tiles on a phone (§5.5).
  const hostsView = ref<HostsView | null>(stored.hostsView ?? null)
  const systemDark = ref(false)

  const media =
    typeof window !== 'undefined' && window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null
  if (media) {
    systemDark.value = media.matches
    media.addEventListener?.('change', (e) => (systemDark.value = e.matches))
  }

  function isDark() {
    return theme.value === 'dark' || (theme.value === 'system' && systemDark.value)
  }

  function apply() {
    document.documentElement.classList.toggle(darkClass, isDark())
  }

  watch([theme, systemDark], apply, { immediate: true })
  watch([theme, drawerCollapsed, hostsView], () =>
    localStorage.setItem(
      storageKey,
      JSON.stringify({ theme: theme.value, drawerCollapsed: drawerCollapsed.value, hostsView: hostsView.value }),
    ),
  )

  /** Light ↔ dark, starting from what is shown now. */
  function toggleTheme() {
    theme.value = isDark() ? 'light' : 'dark'
  }

  return { theme, drawerCollapsed, hostsView, isDark, toggleTheme }
})
