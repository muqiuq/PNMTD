import { describe, expect, it } from 'vitest'
import { mountWithUi } from '@/test/mount'
import StatusBadge, { type Status } from './StatusBadge.vue'
import CodeTag from './CodeTag.vue'

describe('StatusBadge', () => {
  it.each<[Status, string, string]>([
    ['Ok', 'pi-check-circle', 'OK'],
    ['Failing', 'pi-times-circle', 'Failing'],
    ['NoData', 'pi-circle', 'No data'],
    ['Ignored', 'pi-eye-slash', 'Ignored'],
    ['Disabled', 'pi-power-off', 'Disabled'],
    ['Degraded', 'pi-exclamation-triangle', 'Degraded'],
  ])('shows %s with an icon and a word, readable without colour', (status, icon, text) => {
    const badge = mountWithUi(StatusBadge, { props: { status } })
    expect(badge.find('i').classes()).toContain(icon)
    expect(badge.text()).toBe(text)
  })

  it('keeps the word as the accessible name when it shows only the icon', () => {
    const badge = mountWithUi(StatusBadge, { props: { status: 'Failing', iconOnly: true, label: '2 failing' } })
    expect(badge.text()).toBe('')
    expect(badge.attributes('aria-label')).toBe('2 failing')
    expect(badge.attributes('title')).toBe('2 failing')
  })
})

describe('CodeTag', () => {
  it.each([
    [200, 'tone-ok'],
    [399, 'tone-ok'],
    [400, 'tone-fail'],
    [460, 'tone-fail'],
    [-1, 'tone-none'],
    [null, 'tone-none'],
  ])('colours %s by its range', (code, tone) => {
    expect(mountWithUi(CodeTag, { props: { code } }).classes()).toContain(tone)
  })

  it('shows the code with its name, or the name as a tooltip when compact', () => {
    expect(mountWithUi(CodeTag, { props: { code: 460, name: 'Heartbeat missing' } }).text()).toBe(
      '460Heartbeat missing',
    )
    const compact = mountWithUi(CodeTag, { props: { code: 460, name: 'Heartbeat missing', compact: true } })
    expect(compact.text()).toBe('460')
    expect(compact.attributes('title')).toBe('460 Heartbeat missing')
  })
})
