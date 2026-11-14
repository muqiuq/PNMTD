import { describe, expect, it } from 'vitest'
import { visibleNavigation } from './navigation'

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
})
