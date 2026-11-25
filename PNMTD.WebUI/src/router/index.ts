import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { cookieAdminRight, type Right } from '@/api/rights'
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
    // Was this you? (docs/cookie-access-plan.md §6.6): the e-mail's link, without sign-in; only its buttons answer.
    path: '/was-this-you/:token',
    name: 'was-this-you',
    component: () => import('@/views/auth/WasThisYouView.vue'),
    meta: { layout: 'auth', public: true, title: 'Was this you?' },
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
    // Boards (docs/boards-plan.md §10.1): the list, and a board's page, which takes the whole window.
    path: '/boards',
    name: 'boards',
    component: () => import('@/views/boards/BoardsView.vue'),
    meta: { title: 'Boards', right: ['Boards', 'View'] },
  },
  {
    path: '/boards/:id([0-9a-f-]{36})/:tab(layout|bar|look|access)?',
    name: 'board',
    component: () => import('@/views/boards/BoardEditView.vue'),
    meta: { title: 'Board', right: ['Boards', 'View'] },
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
    path: '/hosts/:id/:tab(overview|history)?',
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
    path: '/locations/:id/:tab(overview|information|pages|tickets|history)?',
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
    // The knowledge map (docs/knowledge-base-extensions-plan.md §8).
    path: '/kb/map',
    name: 'kb-map',
    component: () => import('@/views/kb/map/KnowledgeMapView.vue'),
    meta: { title: 'Knowledge map', right: ['KnowledgeBase', 'View'] },
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
    // A MikroTik router's too (docs/mikrotik-plan.md §11.1), and a server's (docs/windows-sensor-plan.md §10.2), and an SSL
    // certificate's (docs/ssl-certificate-plan.md §8.3); an Advanced sensor's Data and Rules (docs/advanced-sensor-plan.md §10.3);
    // a Linux server's OS updates (docs/linux-os-updates-plan.md §10.1).
    path: '/sensors/:id/:tab(history|last-packets|commands|firmware|protocol|setup|backups|updates|events|overview|hardware|network|services|remote-access|access|event-log|journal|ssh-keys|os-updates|diagnostics|changes|agent|scripts|wmi|certificate|data|rules)?',
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
    // Cookie's window (docs/cookie-plan.md §9.2, §21.5): the cases, the decision log, the trace and the chat; ?case= opens
    // a case.
    path: '/cookie/:tab(cases|decisions|trace|chat)?',
    name: 'cookie',
    component: () => import('@/views/cookie/CookieView.vue'),
    meta: { title: 'Cookie', right: ['Cookie', 'Use'] },
  },
  {
    // The lounge (docs/lounge-plan.md §15.2): one conversation, the same for every signed-in user; writing needs Lounge: Use.
    path: '/lounge',
    name: 'lounge',
    component: () => import('@/views/lounge/LoungeView.vue'),
    meta: { title: 'Lounge' },
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
  // Redmine (docs/audit-redmine-zabbix-plan.md §6.3, §6.4): the overview and the search, and a ticket's own page.
  {
    path: '/redmine/:tab(overview|search)?',
    name: 'redmine',
    component: () => import('@/views/redmine/RedmineView.vue'),
    meta: { title: 'Redmine', right: ['Redmine', 'View'] },
  },
  {
    path: '/redmine/issues/:id(\\d+)',
    name: 'redmine-issue',
    component: () => import('@/views/redmine/RedmineIssueView.vue'),
    meta: { title: 'Ticket', right: ['Redmine', 'View'] },
  },
  // Zabbix (docs/audit-redmine-zabbix-plan.md §7.4–7.6, §7.8): the overview, problems, hosts and suggestions, and a host's page.
  {
    path: '/zabbix/:tab(overview|problems|hosts|suggestions)?',
    name: 'zabbix',
    component: () => import('@/views/zabbix/ZabbixView.vue'),
    meta: { title: 'Zabbix', right: ['Zabbix', 'View'] },
  },
  {
    path: '/zabbix/hosts/:hostId(\\d+)',
    name: 'zabbix-host',
    component: () => import('@/views/zabbix/ZabbixHostView.vue'),
    meta: { title: 'Zabbix host', right: ['Zabbix', 'View'] },
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
    path: '/admin/audit-log',
    name: 'audit-log',
    component: () => import('@/views/admin/AuditLogView.vue'),
    meta: { title: 'Audit log', right: ['AuditLog', 'View'] },
  },
  {
    // Admin → MCP (docs/mcp-plan.md §10): the documentation for every signed-in user; the log and the tokens by MCP's rights.
    path: '/admin/mcp/:tab(status|docs|log|tokens)?',
    name: 'admin-mcp',
    component: () => import('@/views/admin/mcp/McpView.vue'),
    meta: { title: 'MCP' },
  },
  {
    // The network map (docs/network-map-plan.md §8.1): the map, connections, suggestions, scans and interfaces in ?tab=.
    path: '/network-map',
    name: 'network-map',
    component: () => import('@/views/network/NetworkMapView.vue'),
    meta: { title: 'Network map', right: ['NetworkMap', 'View'] },
  },
  {
    // 3rd Party Connectors (docs/connectors-plan.md §13.1): the area opens on Calls; a connector's page and a caller's page.
    path: '/connectors/:tab(calls|connectors|callers|docs)?',
    name: 'connectors',
    component: () => import('@/views/connectors/ConnectorsView.vue'),
    meta: { title: '3rd Party Connectors', right: ['Connectors', 'View'] },
  },
  {
    path: '/connectors/connector/:id/:tab(overview|target|arguments|security|calls|usage)?',
    name: 'connector',
    component: () => import('@/views/connectors/ConnectorView.vue'),
    meta: { title: 'Connector', right: ['Connectors', 'View'] },
  },
  {
    path: '/connectors/caller/:id',
    name: 'connector-caller',
    component: () => import('@/views/connectors/CallerView.vue'),
    meta: { title: 'Caller', right: ['Connectors', 'View'] },
  },
  // It was the auth log until 1.4 (docs/audit-redmine-zabbix-plan.md §4.2): old links keep working, with their filters.
  { path: '/admin/auth-log', redirect: (to) => ({ name: 'audit-log', query: to.query }) },
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
    // Servers with an agent, Windows and Linux (docs/linux-sensor-plan.md §11.1; /admin/windows before L1); the agents' versions
    // (docs/agent-update-plan.md §8.1) and the OS advisories (docs/linux-os-updates-plan.md §10.4).
    path: '/admin/servers/:tab(servers|tokens|download|log|scripts|run-as|versions|advisories)?',
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
    // The private CAs (docs/ssl-certificate-plan.md §8.5): the page reads with Certificates' View, its buttons need Manage.
    path: '/admin/certificates',
    name: 'certificates',
    component: () => import('@/views/admin/CertificatesView.vue'),
    meta: { title: 'Certificates', right: ['Certificates', 'View'] },
  },
  {
    // docs/mikrotik-plan.md §11.2: the routers and the tunnel network, ?tab=network|routers.
    path: '/admin/mikrotik',
    name: 'mikrotik',
    component: () => import('@/views/admin/MikrotikView.vue'),
    meta: { title: 'MikroTik & WireGuard', right: ['Mikrotik', 'Manage'] },
  },
  {
    path: '/admin/ai/:tab(requests|queue|settings)?',
    name: 'ai',
    component: () => import('@/views/admin/ai/AiView.vue'),
    meta: { title: 'AI', right: ['Ai', 'Manage'] },
  },
  {
    // Admin → Cookie (docs/cookie-plan.md §9.3, §21.5): one tab per section, each versioned; and mute and suppress. The
    // Redmine tab is Redmine: Manage's, the others Cookie: Manage's (docs/cookie-redmine-tab-plan.md §3.2): the page opens
    // for either, and shows the tabs the reader may see.
    path: '/admin/cookie/:tab(overview|identity|cases|redmine|actions|access|lounge|instructions|lessons|models|rights)?',
    name: 'cookie-admin',
    component: () => import('@/views/admin/cookie/CookieAdminView.vue'),
    meta: { title: 'Cookie', right: cookieAdminRight },
  },
  {
    // Revision 7 moved the working hours to the availability plan's settings, and the contacts to the user.
    path: '/admin/cookie/:tab(hours|notifications)',
    redirect: (to) =>
      to.params.tab === 'hours' ? { name: 'availability', params: { tab: 'settings' } } : { name: 'cookie-admin' },
  },
  {
    // The availability plan (docs/cookie-plan.md §8.2, §9.4): for Availability: View, and for everyone in the plan, who
    // needs no right for it. So no right here: the API decides, and the page says so when the plan is empty for the reader.
    path: '/admin/availability/:tab(plan|people|settings)?',
    name: 'availability',
    component: () => import('@/views/admin/availability/AvailabilityView.vue'),
    meta: { title: 'Availability plan' },
  },
  // Responsibilities (docs/responsibilities-plan.md §9.1): who owns what; information only.
  {
    path: '/admin/responsibilities/:tab(list|links|overview|redmine)?',
    name: 'responsibilities',
    component: () => import('@/views/admin/responsibilities/ResponsibilitiesView.vue'),
    meta: { title: 'Responsibilities', right: ['Responsibilities', 'View'] },
  },
  {
    path: '/admin/responsibilities/:id([0-9a-f-]{36})',
    name: 'responsibility',
    component: () => import('@/views/admin/responsibilities/ResponsibilityView.vue'),
    props: true,
    meta: { title: 'Responsibility', right: ['Responsibilities', 'View'] },
  },
  {
    // The long description on the editing page, as a knowledge base page's (docs/responsibilities-plan.md §9.4).
    path: '/admin/responsibilities/:id([0-9a-f-]{36})/edit',
    name: 'responsibility-edit',
    component: () => import('@/views/admin/responsibilities/ResponsibilityEditView.vue'),
    props: true,
    meta: { title: 'Edit responsibility', right: ['Responsibilities', 'Edit'] },
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
    // docs/board-messages-plan.md §7.2: also for Boards: Message, who sends board messages from here.
    meta: {
      title: 'Debug',
      right: {
        anyOf: [
          ['System', 'Manage'],
          ['Boards', 'Message'],
        ],
      },
    },
  },
  // System health (§5.6) and its Outbound tab (docs/redmine-queue-outbound-plan.md §7.1): /admin/system stays the overview.
  {
    path: '/admin/system/:tab(overview|outbound)?',
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
