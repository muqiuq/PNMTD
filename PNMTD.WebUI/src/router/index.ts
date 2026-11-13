import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import type { Role } from '@/api/auth'
import { useSession } from '@/stores/session'
import { decide } from './guard'

declare module 'vue-router' {
  interface RouteMeta {
    /** AuthLayout (a centred card) for the sign-in screens; AppLayout otherwise. */
    layout?: 'auth'
    /** No sign-in needed (the login screens). */
    public?: boolean
    /** The lowest role that may open it (§4.10); the API enforces the same. */
    role?: Role
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
  { path: '/hosts', name: 'hosts', component: () => import('@/views/hosts/HostsView.vue'), meta: { title: 'Hosts' } },
  {
    path: '/hosts/new',
    name: 'host-new',
    component: () => import('@/views/hosts/HostFormView.vue'),
    meta: { title: 'New host', role: 'User' },
  },
  { path: '/hosts/:id', name: 'host', component: () => import('@/views/hosts/HostView.vue'), meta: { title: 'Host' } },
  {
    path: '/hosts/:id/sensors/new',
    name: 'sensor-new',
    component: () => import('@/views/sensors/SensorFormView.vue'),
    meta: { title: 'New sensor', role: 'User' },
  },
  {
    // A UDP node's subpages are tabs of the same page, each with its route (docs/udp-iot-plan.md §6.2).
    // A MikroTik router's too (docs/mikrotik-plan.md §11.1).
    path: '/sensors/:id/:tab(last-packets|commands|firmware|protocol|setup|backups)?',
    name: 'sensor',
    component: () => import('@/views/sensors/SensorView.vue'),
    meta: { title: 'Sensor' },
  },
  {
    path: '/events',
    name: 'events',
    component: () => import('@/views/events/EventsView.vue'),
    meta: { title: 'Events' },
  },
  {
    path: '/notification-rules',
    name: 'notification-rules',
    component: () => import('@/views/rules/RulesView.vue'),
    meta: { title: 'Notification rules' },
  },
  {
    path: '/notification-rules/new',
    name: 'notification-rule-new',
    component: () => import('@/views/rules/RuleFormView.vue'),
    meta: { title: 'New notification rule', role: 'User' },
  },
  {
    path: '/notification-rules/:id',
    name: 'notification-rule',
    component: () => import('@/views/rules/RuleView.vue'),
    meta: { title: 'Notification rule' },
  },
  {
    path: '/dns-zones',
    name: 'dns-zones',
    component: () => import('@/views/dns/DnsZonesView.vue'),
    meta: { title: 'DNS zones' },
  },
  {
    path: '/dns-zones/new',
    name: 'dns-zone-new',
    component: () => import('@/views/dns/DnsZoneFormView.vue'),
    meta: { title: 'New DNS zone', role: 'User' },
  },
  {
    path: '/dns-zones/:id',
    name: 'dns-zone',
    component: () => import('@/views/dns/DnsZoneView.vue'),
    meta: { title: 'DNS zone' },
  },
  {
    path: '/dns-zones/:id/edit',
    name: 'dns-zone-edit',
    component: () => import('@/views/dns/DnsZoneFormView.vue'),
    meta: { title: 'Edit DNS zone', role: 'User' },
  },
  {
    path: '/mail-inputs',
    name: 'mail-inputs',
    component: () => import('@/views/mail/MailInputsView.vue'),
    meta: { title: 'Mail inputs' },
  },
  {
    path: '/mail-inputs/new',
    name: 'mail-input-new',
    component: () => import('@/views/mail/MailInputView.vue'),
    meta: { title: 'New mail input', role: 'User' },
  },
  {
    path: '/mail-inputs/:id',
    name: 'mail-input',
    component: () => import('@/views/mail/MailInputView.vue'),
    meta: { title: 'Mail input' },
  },
  {
    path: '/mail-log',
    name: 'mail-log',
    component: () => import('@/views/mail/MailLogView.vue'),
    meta: { title: 'Mail log', role: 'User' },
  },
  {
    path: '/admin/key-values',
    name: 'key-values',
    component: () => import('@/views/admin/KeyValuesView.vue'),
    meta: { title: 'Key values', role: 'Admin' },
  },
  {
    path: '/admin/users',
    name: 'users',
    component: () => import('@/views/admin/UsersView.vue'),
    meta: { title: 'Users', role: 'Admin' },
  },
  {
    path: '/admin/auth-log',
    name: 'auth-log',
    component: () => import('@/views/admin/AuthLogView.vue'),
    meta: { title: 'Auth log', role: 'Admin' },
  },
  {
    path: '/admin/udp-nodes',
    name: 'udp-nodes',
    component: () => import('@/views/admin/UdpNodesView.vue'),
    meta: { title: 'UDP nodes', role: 'Admin' },
  },
  {
    // The protocol reference without a node, for every role (the Documentation page links it).
    path: '/admin/udp-nodes/protocol',
    name: 'udp-protocol',
    component: () => import('@/views/admin/UdpProtocolView.vue'),
    meta: { title: 'UDP protocol' },
  },
  {
    path: '/admin/system',
    name: 'system',
    component: () => import('@/views/admin/SystemView.vue'),
    meta: { title: 'System health', role: 'Admin' },
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
    { name: to.name, fullPath: to.fullPath, public: to.meta.public, role: to.meta.role },
    { signedIn: session.user !== null, flags: session.flags, can: session.can },
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
