import { describe, expect, it } from 'vitest'
import { holds, type Level } from '@/api/rights'
import { navigation, visibleNavigation } from './navigation'

describe('visibleNavigation', () => {
  const names = (groups: ReturnType<typeof visibleNavigation>) => groups.flatMap((g) => g.items.map((i) => i.name))

  it('shows an integration only while it is configured, and with its right (§10.1)', () => {
    expect(
      names(
        visibleNavigation(
          () => true,
          () => false,
        ),
      ),
    ).not.toContain('redmine')
    expect(
      names(
        visibleNavigation(
          () => true,
          (f) => f === 'redmine',
        ),
      ),
    ).toContain('redmine')
    expect(
      names(
        visibleNavigation(
          (r) => r !== 'admin' && r[0] !== 'Redmine',
          () => true,
        ),
      ),
    ).not.toContain('redmine')
  })

  it('drops a group without entries', () => {
    const groups = visibleNavigation(
      () => true,
      () => false,
    )
    expect(groups.map((g) => g.label)).not.toContain('Integrations')
  })

  it('leaves out the menus the deployment hides, and only those (Redmine__HideMenu, Mail__HideMenu, Zabbix__HideMenu)', () => {
    const all = names(
      visibleNavigation(
        () => true,
        () => true,
      ),
    )
    expect(all).toEqual(expect.arrayContaining(['redmine', 'zabbix', 'mail-inputs', 'mail-log']))
    const hidden = names(
      visibleNavigation(
        () => true,
        () => true,
        ['mail', 'zabbix'],
      ),
    )
    expect(hidden).not.toContain('mail-inputs')
    expect(hidden).not.toContain('mail-log')
    expect(hidden).not.toContain('zabbix')
    expect(hidden).toContain('redmine')
    expect(all.length - hidden.length).toBe(3)
  })

  it('has the groups of docs/menu-proposal.md, Devices and Admin folded at first, and every icon once', () => {
    expect(navigation.map((g) => g.label)).toEqual([
      'Overview',
      'Monitoring',
      'Alerts',
      'Mail',
      'Knowledge',
      'Integrations',
      'Devices',
      'Admin',
    ])
    expect(navigation.filter((g) => g.folded).map((g) => g.label)).toEqual(['Devices', 'Admin'])
    const icons = navigation.flatMap((g) => g.items.map((i) => i.icon))
    expect(new Set(icons).size).toBe(icons.length)
    expect(navigation.flatMap((g) => g.items.map((i) => i.name))).not.toContain('docs')
  })

  it("shows Cookie's window with Cookie's Use and Admin → Cookie only with its Manage (docs/cookie-plan.md §10.1)", () => {
    const use = names(visibleNavigation((r) => r !== 'admin' && r[0] === 'Cookie' && r[1] === 'Use'))
    expect(use).toEqual(['dashboard', 'cookie'])
    const manage = names(visibleNavigation((r) => r !== 'admin' && r[0] === 'Cookie'))
    expect(manage).toEqual(['dashboard', 'cookie', 'cookie-admin'])
  })

  it('shows the availability plan with its View, and to everyone in the plan without a right (docs/cookie-plan.md §9.4)', () => {
    const none = () => false
    expect(names(visibleNavigation(none))).toEqual(['dashboard'])
    expect(names(visibleNavigation(none, () => true, [], true))).toEqual(['dashboard', 'availability'])
    const view = names(visibleNavigation((r) => r !== 'admin' && r[0] === 'Availability'))
    expect(view).toEqual(['dashboard', 'availability'])
  })

  it('shows Admin → Certificates with Certificates: Manage only (docs/ssl-certificate-plan.md §8.5)', () => {
    const as = (level: Level) =>
      names(visibleNavigation((r) => r !== 'admin' && holds(false, { Certificates: level }, r[0], r[1])))
    expect(as('View')).not.toContain('certificates')
    expect(as('Edit')).not.toContain('certificates')
    expect(as('Manage')).toEqual(['dashboard', 'certificates'])
  })

  it('drops the Mail group when Mail__HideMenu hides its entries', () => {
    expect(
      visibleNavigation(
        () => true,
        () => true,
        ['mail'],
      ).map((g) => g.label),
    ).not.toContain('Mail')
  })
})
