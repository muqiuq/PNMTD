import { describe, expect, it } from 'vitest'
import { mountWithUi } from '@/test/mount'
import CookieNotesPanel from './CookieNotesPanel.vue'

const notes = [
  {
    id: 1,
    at: '2026-11-15T10:30:00Z',
    text: 'The backup runs at 02:00; the heartbeat is late then.',
    caseId: 'c1',
    caseTitle: 'web-01: ping',
    areas: ['Monitoring'],
  },
  { id: 2, at: '2026-11-14T09:00:00Z', text: 'Flaps at night.', caseId: null, caseTitle: null, areas: ['Monitoring'] },
]

describe("Cookie's notes panel", () => {
  it('hides itself when there is nothing to show', () => {
    const panel = mountWithUi(CookieNotesPanel, { props: { notes: [], canRemove: true, canLinkCase: true } })
    expect(panel.find('section').exists()).toBe(false)
  })

  it('shows each note, the case link only with Cookie: Use, and Remove only with Monitoring: Edit', async () => {
    const reader = mountWithUi(CookieNotesPanel, { props: { notes, canRemove: false, canLinkCase: false } })
    expect(reader.findAll('li')).toHaveLength(2)
    expect(reader.text()).toContain('The backup runs at 02:00')
    expect(reader.find('a').exists()).toBe(false)
    expect(reader.find('button').exists()).toBe(false)

    const editor = mountWithUi(CookieNotesPanel, { props: { notes, canRemove: true, canLinkCase: true } })
    expect(editor.findAll('a')).toHaveLength(1)
    expect(editor.find('a').text()).toBe('Case: web-01: ping')
    expect(editor.findAll('button')).toHaveLength(2)
    await editor.findAll('button')[1]!.trigger('click')
    expect(editor.emitted('remove')?.[0]).toEqual([notes[1]])
  })
})
