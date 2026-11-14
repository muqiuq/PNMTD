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
})
