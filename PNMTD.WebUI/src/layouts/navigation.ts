import type { Role } from '@/api/auth'

export interface NavItem {
  label: string
  icon: string
  to: string
  name: string
  role?: Role
}

export interface NavGroup {
  label: string
  items: NavItem[]
}

/** The drawer (§5.4): the groups of today, minus what the role cannot use. */
export const navigation: NavGroup[] = [
  {
    label: 'Monitoring',
    items: [
      { label: 'Dashboard', icon: 'pi pi-th-large', to: '/', name: 'dashboard' },
      { label: 'Hosts', icon: 'pi pi-server', to: '/hosts', name: 'hosts' },
      { label: 'Events', icon: 'pi pi-list', to: '/events', name: 'events' },
      { label: 'DNS zones', icon: 'pi pi-globe', to: '/dns-zones', name: 'dns-zones' },
      { label: 'Mail inputs', icon: 'pi pi-envelope', to: '/mail-inputs', name: 'mail-inputs' },
      { label: 'Mail log', icon: 'pi pi-inbox', to: '/mail-log', name: 'mail-log', role: 'User' },
      { label: 'Notification rules', icon: 'pi pi-bell', to: '/notification-rules', name: 'notification-rules' },
      { label: 'Notification manager', icon: 'pi pi-th-large', to: '/notifications', name: 'notifications' },
      { label: 'Uplink', icon: 'pi pi-cloud', to: '/uplink', name: 'uplink' },
    ],
  },
  {
    label: 'Admin',
    items: [
      { label: 'Key values', icon: 'pi pi-key', to: '/admin/key-values', name: 'key-values', role: 'Admin' },
      { label: 'Users', icon: 'pi pi-users', to: '/admin/users', name: 'users', role: 'Admin' },
      { label: 'Auth log', icon: 'pi pi-history', to: '/admin/auth-log', name: 'auth-log', role: 'Admin' },
      { label: 'UDP nodes', icon: 'pi pi-wifi', to: '/admin/udp-nodes', name: 'udp-nodes', role: 'Admin' },
      { label: 'MikroTik & WireGuard', icon: 'pi pi-server', to: '/admin/mikrotik', name: 'mikrotik', role: 'Admin' },
      { label: 'System health', icon: 'pi pi-heart', to: '/admin/system', name: 'system', role: 'Admin' },
      { label: 'AI', icon: 'pi pi-sparkles', to: '/admin/ai', name: 'ai', role: 'Admin' },
      { label: 'Debug', icon: 'pi pi-wrench', to: '/admin/debug', name: 'debug', role: 'Admin' },
    ],
  },
  {
    label: 'Help',
    items: [{ label: 'Documentation', icon: 'pi pi-book', to: '/docs', name: 'docs' }],
  },
]

export function visibleNavigation(can: (role: Role) => boolean): NavGroup[] {
  return navigation
    .map((g) => ({ ...g, items: g.items.filter((i) => !i.role || can(i.role)) }))
    .filter((g) => g.items.length > 0)
}
