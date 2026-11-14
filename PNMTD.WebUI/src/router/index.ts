import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import type { Right } from '@/api/rights'
import { useSession } from '@/stores/session'
import { decide } from './guard'

declare module 'vue-router' {
  interface RouteMeta {
    /** AuthLayout (a centred card) for the sign-in screens; AppLayout otherwise. */
    layout?: 'auth'
    /** No sign-in needed (the login screens). */
    public?: boolean
    /** What it needs (docs/audit-redmine-zabbix-plan.md §5.4); the API enforces the same. */
    right?: Right
    title?: string
  }
}

/** The routes of §5.6, relative to /pnmt-admin. */
export const routes: RouteRecordRaw[] = [
  // The sign-in screens, in AuthLayout (App.vue picks the layout by meta.layout).
  {
    path: '/login',
    name: 'login',
    component: () => import('@/views/auth/LoginView.vue'),
    meta: { layout: 'auth', public: true, title: 'Sign in' },
  },
  {
    path: '/login/2fa',
    name: 'login-2fa',
    component: () => import('@/views/auth/LoginTotpView.vue'),
    meta: { layout: 'auth', public: true, title: 'Sign in' },
  },
  {
    path: '/change-password',
    name: 'change-password',
    component: () => import('@/views/auth/ChangePasswordView.vue'),
    meta: { layout: 'auth', title: 'Change your password' },
  },
  {
    path: '/account/setup-2fa',
    name: 'setup-2fa',
    component: () => import('@/views/account/SetupTotpView.vue'),
    meta: { layout: 'auth', title: 'Two-factor authentication' },
  },

  // Everything else, in AppLayout.
  {
    path: '/',
    name: 'dashboard',
    component: () => import('@/views/dashboard/DashboardView.vue'),
    meta: { title: 'Dashboard' },
  },
  {
    path: '/hosts',
    name: 'hosts',
    component: () => import('@/views/hosts/HostsView.vue'),
    meta: { title: 'Hosts', right: ['Monitoring', 'View'] },
  },
  {
    path: '/hosts/new',
    name: 'host-new',
    component: () => import('@/views/hosts/HostFormView.vue'),
    meta: { title: 'New host', right: ['Monitoring', 'Edit'] },
  },
  {
    path: '/hosts/:id',
    name: 'host',
    component: () => import('@/views/hosts/HostView.vue'),
    meta: { title: 'Host', right: ['Monitoring', 'View'] },
  },
  // Locations (docs/locations-knowledge-plan.md §10.2): the tree, and a location's page with its tabs.
  {
    path: '/locations',
    name: 'locations',
    component: () => import('@/views/locations/LocationsView.vue'),
    meta: { title: 'Locations', right: ['Locations', 'View'] },
  },
  {
    path: '/locations/:id/:tab(overview|information|pages|history)?',
    name: 'location',
    component: () => import('@/views/locations/LocationView.vue'),
    meta: { title: 'Location', right: ['Locations', 'View'] },
  },
  // The knowledge base (docs/locations-knowledge-plan.md §10.2): the list and search, a page by its number, editing it on
  // the editing page, its history (?from&to).
  {
    path: '/kb',
    name: 'kb',
    component: () => import('@/views/kb/KbListView.vue'),
    meta: { title: 'Knowledge base', right: ['KnowledgeBase', 'View'] },
  },
  {
    path: '/kb/new',
    name: 'kb-new',
    component: () => import('@/views/kb/KbEditView.vue'),
    meta: { title: 'New page', right: ['KnowledgeBase', 'Edit'] },
  },
  {
    path: '/kb/:number(\\d+)',
    name: 'kb-page',
    component: () => import('@/views/kb/KbPageView.vue'),
    meta: { title: 'Knowledge base', right: ['KnowledgeBase', 'View'] },
  },
  {
    path: '/kb/:number(\\d+)/edit',
    name: 'kb-edit',
    component: () => import('@/views/kb/KbEditView.vue'),
    meta: { title: 'Edit page', right: ['KnowledgeBase', 'Edit'] },
  },
  {
    path: '/kb/:number(\\d+)/history',
    name: 'kb-history',
    component: () => import('@/views/kb/KbHistoryView.vue'),
    meta: { title: 'History', right: ['KnowledgeBase', 'View'] },
  },
  {
    // A Markdown field on the editing page (§5.9).
    path: '/locations/:id/fields/:key/edit',
    name: 'location-field-edit',
    component: () => import('@/views/locations/LocationFieldEditView.vue'),
    meta: { title: 'Edit field', right: ['Locations', 'Edit'] },
  },
  {
    path: '/hosts/:id/sensors/new',
    name: 'sensor-new',
    component: () => import('@/views/sensors/SensorFormView.vue'),
    meta: { title: 'New sensor', right: ['Monitoring', 'Edit'] },
  },
  {
    // A UDP node's subpages are tabs of the same page, each with its route (docs/udp-iot-plan.md §6.2).
    // A MikroTik router's too (docs/mikrotik-plan.md §11.1), and a server's (docs/windows-sensor-plan.md §10.2).
    path: '/sensors/:id/:tab(last-packets|commands|firmware|protocol|setup|backups|updates|events|overview|hardware|network|services|remote-access|access|event-log|journal|ssh-keys|diagnostics|changes|agent|scripts|wmi)?',
    name: 'sensor',
    component: () => import('@/views/sensors/SensorView.vue'),
    meta: { title: 'Sensor', right: ['Monitoring', 'View'] },
  },
  {
    path: '/events',
    name: 'events',
    component: () => import('@/views/events/EventsView.vue'),
    meta: { title: 'Events', right: ['Monitoring', 'View'] },
  },
  {
    path: '/notification-rules',
    name: 'notification-rules',
    component: () => import('@/views/rules/RulesView.vue'),
    meta: { title: 'Notification rules', right: ['Monitoring', 'View'] },
  },
  {
    path: '/notification-rules/new',
    name: 'notification-rule-new',
    component: () => import('@/views/rules/RuleFormView.vue'),
    meta: { title: 'New notification rule', right: ['Monitoring', 'Edit'] },
  },
  {
    path: '/notification-rules/:id',
    name: 'notification-rule',
    component: () => import('@/views/rules/RuleView.vue'),
    meta: { title: 'Notification rule', right: ['Monitoring', 'View'] },
  },
  {
    path: '/dns-zones',
    name: 'dns-zones',
    component: () => import('@/views/dns/DnsZonesView.vue'),
    meta: { title: 'DNS zones', right: ['Monitoring', 'View'] },
  },
  {
    path: '/dns-zones/new',
    name: 'dns-zone-new',
    component: () => import('@/views/dns/DnsZoneFormView.vue'),
    meta: { title: 'New DNS zone', right: ['Monitoring', 'Edit'] },
  },
  {
    path: '/dns-zones/:id',
    name: 'dns-zone',
    component: () => import('@/views/dns/DnsZoneView.vue'),
    meta: { title: 'DNS zone', right: ['Monitoring', 'View'] },
  },
  {
    path: '/dns-zones/:id/edit',
    name: 'dns-zone-edit',
    component: () => import('@/views/dns/DnsZoneFormView.vue'),
    meta: { title: 'Edit DNS zone', right: ['Monitoring', 'Edit'] },
  },
  {
    path: '/mail-inputs',
    name: 'mail-inputs',
    component: () => import('@/views/mail/MailInputsView.vue'),
    meta: { title: 'Mail inputs', right: ['Monitoring', 'View'] },
  },
  {
    path: '/mail-inputs/new',
    name: 'mail-input-new',
    component: () => import('@/views/mail/MailInputView.vue'),
    meta: { title: 'New mail input', right: ['Monitoring', 'Edit'] },
  },
  {
    path: '/mail-inputs/:id',
    name: 'mail-input',
    component: () => import('@/views/mail/MailInputView.vue'),
    meta: { title: 'Mail input', right: ['Monitoring', 'View'] },
  },
  {
    path: '/mail-log',
    name: 'mail-log',
    component: () => import('@/views/mail/MailLogView.vue'),
    meta: { title: 'Mail log', right: ['Mail', 'Read'] },
  },
  {
    path: '/decisions/:id?',
    name: 'decisions',
    component: () => import('@/views/decisions/DecisionsView.vue'),
    meta: { title: 'Decisions', right: ['Decisions', 'Decide'] },
  },
  {
    path: '/notifications',
    name: 'notifications',
    component: () => import('@/views/notifications/NotificationsView.vue'),
    meta: { title: 'Notifications', right: ['Monitoring', 'View'] },
  },
  {
    path: '/uplink',
    name: 'uplink',
    component: () => import('@/views/uplink/UplinkView.vue'),
    meta: { title: 'Uplink', right: ['Monitoring', 'View'] },
  },
  {
    path: '/admin/key-values',
    name: 'key-values',
    component: () => import('@/views/admin/KeyValuesView.vue'),
    meta: { title: 'Key values', right: ['System', 'Manage'] },
  },
  {
    path: '/admin/users',
    name: 'users',
    component: () => import('@/views/admin/UsersView.vue'),
    meta: { title: 'Users', right: 'admin' },
  },
  // Groups and rights (docs/audit-redmine-zabbix-plan.md §5.5): admins only.
  {
    path: '/admin/groups',
    name: 'groups',
    component: () => import('@/views/admin/groups/GroupsView.vue'),
    meta: { title: 'Groups', right: 'admin' },
  },
  {
    path: '/admin/groups/:id',
    name: 'group',
    component: () => import('@/views/admin/groups/GroupView.vue'),
    props: true,
    meta: { title: 'Group', right: 'admin' },
  },
  {
    path: '/admin/auth-log',
    name: 'auth-log',
    component: () => import('@/views/admin/AuthLogView.vue'),
    meta: { title: 'Auth log', right: ['AuditLog', 'View'] },
  },
  {
    path: '/admin/udp-nodes',
    name: 'udp-nodes',
    component: () => import('@/views/admin/UdpNodesView.vue'),
    meta: { title: 'UDP nodes', right: ['UdpNodes', 'Manage'] },
  },
  {
    // The protocol reference without a node, for everyone who sees monitoring (the Documentation page links it).
    path: '/admin/udp-nodes/protocol',
    name: 'udp-protocol',
    component: () => import('@/views/admin/UdpProtocolView.vue'),
    meta: { title: 'UDP protocol', right: ['Monitoring', 'View'] },
  },
  {
    path: '/admin/diagnostics/:tab(sets|assignments|runs)?',
    name: 'diagnostics',
    component: () => import('@/views/admin/diagnostics/DiagnosticsView.vue'),
    meta: { title: 'Diagnostics', right: ['Diagnostics', 'Manage'] },
  },
  {
    // Servers with an agent, Windows and Linux (docs/linux-sensor-plan.md §11.1; /admin/windows before L1).
    path: '/admin/servers/:tab(servers|tokens|download|log|scripts|run-as)?',
    name: 'servers',
    component: () => import('@/views/admin/servers/ServersView.vue'),
    meta: { title: 'Servers', right: ['Servers', 'Manage'] },
  },
  {
    // The SSH key manager (docs/linux-sensor-plan.md §11.4): the register, unregistered keys, removals; ?key= opens a key.
    path: '/admin/ssh-keys/:tab(keys|unregistered|removals)?',
    name: 'ssh-keys',
    component: () => import('@/views/admin/sshkeys/SshKeysView.vue'),
    meta: { title: 'SSH keys', right: ['SshKeys', 'Manage'] },
  },
  {
    // docs/mikrotik-plan.md §11.2: the routers and the tunnel network, ?tab=network|routers.
    path: '/admin/mikrotik',
    name: 'mikrotik',
    component: () => import('@/views/admin/MikrotikView.vue'),
    meta: { title: 'MikroTik & WireGuard', right: ['Mikrotik', 'Manage'] },
  },
  {
    path: '/admin/ai/:tab(requests|queue|analysis|settings)?',
    name: 'ai',
    component: () => import('@/views/admin/ai/AiView.vue'),
    meta: { title: 'AI', right: ['Ai', 'Manage'] },
  },
  {
    path: '/admin/location-fields',
    name: 'location-fields',
    component: () => import('@/views/admin/locationFields/LocationFieldsView.vue'),
    meta: { title: 'Location fields', right: ['Locations', 'Manage'] },
  },
  {
    // Admin → Knowledge base (docs/locations-knowledge-plan.md §10.6): the tags and the files.
    path: '/admin/knowledge-base/:tab(tags|files)?',
    name: 'kb-admin',
    component: () => import('@/views/admin/kb/KnowledgeBaseAdminView.vue'),
    meta: { title: 'Knowledge base', right: ['KnowledgeBase', 'Manage'] },
  },
  {
    path: '/admin/debug',
    name: 'debug',
    component: () => import('@/views/admin/DebugView.vue'),
    meta: { title: 'Debug', right: ['System', 'Manage'] },
  },
  {
    path: '/admin/system',
    name: 'system',
    component: () => import('@/views/admin/SystemView.vue'),
    meta: { title: 'System health', right: ['System', 'Manage'] },
  },
  {
    path: '/docs',
    name: 'docs',
    component: () => import('@/views/docs/DocsView.vue'),
    meta: { title: 'Documentation' },
  },
  {
    path: '/account',
    name: 'account',
    component: () => import('@/views/account/AccountView.vue'),
    meta: { title: 'Account' },
  },
  {
    // A user's own custom tunnels (docs/mikrotik-plan.md §11.3).
    path: '/account/tunnels',
    name: 'account-tunnels',
    component: () => import('@/views/account/TunnelsView.vue'),
    meta: { title: 'Tunnels' },
  },
  {
    path: '/account/security',
    name: 'account-security',
    component: () => import('@/views/account/SecurityView.vue'),
    meta: { title: 'Security' },
  },
  {
    path: '/:pathMatch(.*)*',
    name: 'not-found',
    component: () => import('@/views/NotFoundView.vue'),
    meta: { title: 'Not found' },
  },
]

export const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes,
  scrollBehavior: (_to, _from, saved) => saved ?? { top: 0 },
})

router.beforeEach(async (to) => {
  const session = useSession()
  await session.load()
  return decide(
    { name: to.name, fullPath: to.fullPath, public: to.meta.public, right: to.meta.right },
    { signedIn: session.user !== null, flags: session.flags, allows: session.allows },
  )
})

/** "Hosts · PNMT · <identity>"; the dashboard puts the failure count in front (§5.4). */
export function pageTitle(title: string | undefined, identity: string, failing = 0): string {
  const parts = [title, 'PNMT', identity].filter((p) => p)
  return (failing > 0 ? `(${failing}) ` : '') + parts.join(' · ')
}

// AppLayout keeps the title itself, with the failure count in front.
router.afterEach((to) => {
  if (to.meta.layout === 'auth') {
    document.title = pageTitle(to.meta.title, useSession().identity)
  }
})
