import { describe, expect, it } from 'vitest'
import { holds, meets, type Area, type Level, type Right } from '@/api/rights'
import { navigation, visibleNavigation } from './navigation'

describe('visibleNavigation', () => {
  const names = (groups: ReturnType<typeof visibleNavigation>) => groups.flatMap((g) => g.items.map((i) => i.name))
  /** A reader without the admin flag whose levels are those the predicate accepts. */
  const by = (accepts: (area: Area, level: Level) => boolean) => (r: Right) => r !== 'admin' && meets(r, false, accepts)

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
          by((area) => area !== 'Redmine'),
          () => true,
        ),
      ),
    ).not.toContain('redmine')
  })

  it('drops a group without entries', () => {
    // Without Redmine and Zabbix configured, and without the connectors' right (docs/connectors-plan.md §13.1).
    const groups = visibleNavigation(
      (r) => r === 'admin' || meets(r, false, (area) => area !== 'Connectors'),
      () => false,
    )
    expect(groups.map((g) => g.label)).not.toContain('Integrations')
  })

  it("shows 3rd Party Connectors in Integrations with the connectors' View, configured or not (docs/connectors-plan.md §13.1)", () => {
    const view = visibleNavigation(
      by((area, level) => holds(false, { Connectors: 'View' }, area, level)),
      () => false,
    )
    expect(view.find((g) => g.label === 'Integrations')?.items.map((i) => i.name)).toEqual(['connectors'])
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

  it('shows the network map with its View (docs/network-map-plan.md §8.1)', () => {
    expect(names(visibleNavigation(by((area, level) => area === 'NetworkMap' && level === 'View')))).toEqual([
      'dashboard',
      'lounge',
      'network-map',
    ])
    expect(names(visibleNavigation(by((area) => area === 'Monitoring')))).not.toContain('network-map')
  })

  it("shows Cookie's window with Cookie's Use and Admin → Cookie only with its Manage (docs/cookie-plan.md §10.1)", () => {
    const use = names(visibleNavigation(by((area, level) => area === 'Cookie' && level === 'Use')))
    expect(use).toEqual(['dashboard', 'cookie', 'lounge'])
    const manage = names(visibleNavigation(by((area) => area === 'Cookie')))
    expect(manage).toEqual(['dashboard', 'cookie', 'lounge', 'cookie-admin'])
    // Redmine: Manage opens Admin → Cookie for its Redmine tab (docs/cookie-redmine-tab-plan.md §3.2), not Cookie's window.
    const redmine = names(visibleNavigation(by((area, level) => holds(false, { Redmine: 'Manage' }, area, level))))
    expect(redmine).toContain('cookie-admin')
    expect(redmine).not.toContain('cookie')
    expect(names(visibleNavigation(by((area, level) => holds(false, { Redmine: 'Link' }, area, level))))).not.toContain(
      'cookie-admin',
    )
  })

  it('shows the availability plan with its View, and to everyone in the plan without a right (docs/cookie-plan.md §9.4)', () => {
    const none = () => false
    expect(names(visibleNavigation(none))).toEqual(['dashboard', 'lounge'])
    expect(names(visibleNavigation(none, () => true, [], true))).toEqual(['dashboard', 'lounge', 'availability'])
    const view = names(visibleNavigation(by((area) => area === 'Availability')))
    expect(view).toEqual(['dashboard', 'lounge', 'availability'])
  })

  it('shows Admin → Certificates with Certificates: Manage only (docs/ssl-certificate-plan.md §8.5)', () => {
    const as = (level: Level) =>
      names(visibleNavigation(by((area, l) => holds(false, { Certificates: level }, area, l))))
    expect(as('View')).not.toContain('certificates')
    expect(as('Edit')).not.toContain('certificates')
    expect(as('Manage')).toEqual(['dashboard', 'lounge', 'certificates'])
  })

  it('shows the lounge to every signed-in user, without a right (docs/lounge-plan.md §15.1)', () => {
    const overview = visibleNavigation(() => false).find((g) => g.label === 'Overview')
    expect(overview?.items.map((i) => i.name)).toEqual(['dashboard', 'lounge'])
    const all = visibleNavigation(() => true).find((g) => g.label === 'Overview')
    // Boards (docs/boards-plan.md §10.1) after the dashboard, with Boards: View.
    expect(all?.items.map((i) => i.name)).toEqual(['dashboard', 'boards', 'decisions', 'events', 'cookie', 'lounge'])
    expect(navigation.flatMap((g) => g.items).find((i) => i.name === 'lounge')?.right).toBeUndefined()
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
  it('shows Licences & services in Knowledge only while the feature is on, and with its View (docs/licences-plan.md §8.1, §13.2)', () => {
    const everything = () => true
    expect(names(visibleNavigation(everything, (f) => f !== 'licenses'))).not.toContain('licences')
    expect(names(visibleNavigation(everything, everything))).toContain('licences')
    expect(
      names(
        visibleNavigation(
          by((area) => area !== 'Licenses'),
          everything,
        ),
      ),
    ).not.toContain('licences')
    expect(
      visibleNavigation(everything, everything)
        .find((g) => g.label === 'Knowledge')
        ?.items.map((i) => i.name),
    ).toEqual(['kb', 'licences'])
  })
})
