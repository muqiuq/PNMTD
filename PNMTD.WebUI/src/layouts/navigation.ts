import type { Right } from '@/api/rights'

export interface NavItem {
  label: string
  icon: string
  to: string
  name: string
  /** What its page needs (docs/audit-redmine-zabbix-plan.md §10.1); without, every signed-in user's. */
  right?: Right
  /** An integration's page: only while it is configured (§10.1). */
  feature?: 'redmine' | 'zabbix'
  /** A menu the deployment can hide (Redmine__HideMenu, Mail__HideMenu, Zabbix__HideMenu): only the entry goes. */
  menu?: 'redmine' | 'mail' | 'zabbix'
  /** Also for everyone in the availability plan, who needs no right for it (docs/cookie-plan.md §9.4). */
  inPlan?: boolean
}

export interface NavGroup {
  label: string
  /** Starts folded: Devices and Admin (docs/menu-proposal.md). Each browser remembers what was folded or opened. */
  folded?: boolean
  items: NavItem[]
}

/**
 * The drawer (docs/menu-proposal.md): what is looked at daily first, then setup, devices and the admin pages; minus what the
 * user lacks the right for (docs/audit-redmine-zabbix-plan.md §10.1). Every icon is used once. Documentation is in the
 * user menu.
 */
export const navigation: NavGroup[] = [
  {
    label: 'Overview',
    items: [
      { label: 'Dashboard', icon: 'pi pi-th-large', to: '/', name: 'dashboard' },
      { label: 'Decisions', icon: 'pi pi-inbox', to: '/decisions', name: 'decisions', right: ['Decisions', 'Decide'] },
      { label: 'Events', icon: 'pi pi-list', to: '/events', name: 'events', right: ['Monitoring', 'View'] },
      { label: 'Cookie', icon: 'pi pi-microchip-ai', to: '/cookie', name: 'cookie', right: ['Cookie', 'Use'] },
    ],
  },
  {
    label: 'Monitoring',
    items: [
      { label: 'Hosts', icon: 'pi pi-server', to: '/hosts', name: 'hosts', right: ['Monitoring', 'View'] },
      {
        label: 'Locations',
        icon: 'pi pi-map-marker',
        to: '/locations',
        name: 'locations',
        right: ['Locations', 'View'],
      },
      { label: 'DNS zones', icon: 'pi pi-globe', to: '/dns-zones', name: 'dns-zones', right: ['Monitoring', 'View'] },
      { label: 'Uplink', icon: 'pi pi-cloud', to: '/uplink', name: 'uplink', right: ['Monitoring', 'View'] },
    ],
  },
  {
    label: 'Alerts',
    items: [
      {
        label: 'Notification rules',
        icon: 'pi pi-bell',
        to: '/notification-rules',
        name: 'notification-rules',
        right: ['Monitoring', 'View'],
      },
      {
        label: 'Notification manager',
        icon: 'pi pi-sliders-h',
        to: '/notifications',
        name: 'notifications',
        right: ['Monitoring', 'View'],
      },
    ],
  },
  {
    label: 'Mail',
    items: [
      {
        label: 'Mail inputs',
        icon: 'pi pi-filter',
        to: '/mail-inputs',
        name: 'mail-inputs',
        right: ['Monitoring', 'View'],
        menu: 'mail',
      },
      {
        label: 'Mail log',
        icon: 'pi pi-envelope',
        to: '/mail-log',
        name: 'mail-log',
        right: ['Mail', 'Read'],
        menu: 'mail',
      },
    ],
  },
  {
    // The knowledge base (docs/locations-knowledge-plan.md §10.1).
    label: 'Knowledge',
    items: [{ label: 'Knowledge base', icon: 'pi pi-book', to: '/kb', name: 'kb', right: ['KnowledgeBase', 'View'] }],
  },
  {
    // Redmine and Zabbix (docs/audit-redmine-zabbix-plan.md §10.1): each only when configured.
    label: 'Integrations',
    items: [
      {
        label: 'Redmine',
        icon: 'pi pi-ticket',
        to: '/redmine',
        name: 'redmine',
        right: ['Redmine', 'View'],
        feature: 'redmine',
        menu: 'redmine',
      },
      {
        label: 'Zabbix',
        icon: 'pi pi-chart-bar',
        to: '/zabbix',
        name: 'zabbix',
        right: ['Zabbix', 'View'],
        feature: 'zabbix',
        menu: 'zabbix',
      },
    ],
  },
  {
    label: 'Devices',
    folded: true,
    items: [
      { label: 'Servers', icon: 'pi pi-desktop', to: '/admin/servers', name: 'servers', right: ['Servers', 'Manage'] },
      {
        label: 'MikroTik & WireGuard',
        icon: 'pi pi-share-alt',
        to: '/admin/mikrotik',
        name: 'mikrotik',
        right: ['Mikrotik', 'Manage'],
      },
      {
        label: 'UDP nodes',
        icon: 'pi pi-wifi',
        to: '/admin/udp-nodes',
        name: 'udp-nodes',
        right: ['UdpNodes', 'Manage'],
      },
      { label: 'SSH keys', icon: 'pi pi-key', to: '/admin/ssh-keys', name: 'ssh-keys', right: ['SshKeys', 'Manage'] },
      {
        label: 'Diagnostics',
        icon: 'pi pi-check-circle',
        to: '/admin/diagnostics',
        name: 'diagnostics',
        right: ['Diagnostics', 'Manage'],
      },
    ],
  },
  {
    label: 'Admin',
    folded: true,
    items: [
      { label: 'Users', icon: 'pi pi-users', to: '/admin/users', name: 'users', right: 'admin' },
      { label: 'Groups', icon: 'pi pi-sitemap', to: '/admin/groups', name: 'groups', right: 'admin' },
      {
        label: 'Audit log',
        icon: 'pi pi-history',
        to: '/admin/audit-log',
        name: 'audit-log',
        right: ['AuditLog', 'View'],
      },
      { label: 'System health', icon: 'pi pi-heart', to: '/admin/system', name: 'system', right: ['System', 'Manage'] },
      { label: 'AI', icon: 'pi pi-sparkles', to: '/admin/ai', name: 'ai', right: ['Ai', 'Manage'] },
      { label: 'Cookie', icon: 'pi pi-cog', to: '/admin/cookie', name: 'cookie-admin', right: ['Cookie', 'Manage'] },
      {
        label: 'Availability plan',
        icon: 'pi pi-calendar',
        to: '/admin/availability',
        name: 'availability',
        right: ['Availability', 'View'],
        inPlan: true,
      },
      {
        label: 'Location fields',
        icon: 'pi pi-list-check',
        to: '/admin/location-fields',
        name: 'location-fields',
        right: ['Locations', 'Manage'],
      },
      {
        label: 'Knowledge base settings',
        icon: 'pi pi-tags',
        to: '/admin/knowledge-base',
        name: 'kb-admin',
        right: ['KnowledgeBase', 'Manage'],
      },
      {
        label: 'Key values',
        icon: 'pi pi-database',
        to: '/admin/key-values',
        name: 'key-values',
        right: ['System', 'Manage'],
      },
      { label: 'Debug', icon: 'pi pi-wrench', to: '/admin/debug', name: 'debug', right: ['System', 'Manage'] },
    ],
  },
]

export function visibleNavigation(
  allows: (right: Right) => boolean,
  configured: (feature: 'redmine' | 'zabbix') => boolean = () => true,
  hiddenMenus: readonly string[] = [],
  inPlan = false,
): NavGroup[] {
  return navigation
    .map((g) => ({
      ...g,
      items: g.items.filter(
        (i) =>
          (!i.right || allows(i.right) || (!!i.inPlan && inPlan)) &&
          (!i.feature || configured(i.feature)) &&
          (!i.menu || !hiddenMenus.includes(i.menu)),
      ),
    }))
    .filter((g) => g.items.length > 0)
}
