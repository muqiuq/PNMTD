import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { newElement } from '../layout'
import { boardTime } from '../time'
import type { BoardElementData, BoardElementSettings } from '../types'
import OnDuty from './OnDuty.vue'

/** docs/board-messages-plan.md §9.5: the labels, the texts, names hidden, and the next open day. */
const time = boardTime('Europe/Zurich', { dateLanguage: 'English' })

function show(onDuty: BoardElementData['onDuty'], settings: Partial<BoardElementSettings> = {}) {
  const element = newElement('OnDuty')
  element.settings = { ...element.settings, ...settings }
  const data: BoardElementData = { id: element.id, type: 'OnDuty', state: 'Ok', more: 0, onDuty }
  return mount(OnDuty, { props: { element, data, time } })
}

/** Each row's parts, as the screen shows them apart. */
const rows = (w: ReturnType<typeof show>) => w.findAll('li').map((li) => li.findAll('span').map((x) => x.text()))

const anna = { name: 'Anna Muster', deputy: false, as: 'responsible', where: 'on site' }
const ben = { name: 'Ben Keller', deputy: true, as: 'fallback', where: null }

describe('On duty today', () => {
  it('shows the one on duty and the deputies with their labels', () => {
    const w = show({ people: [anna, ben], as: 'responsible', closed: false })
    expect(rows(w)).toEqual([
      ['Anna Muster', 'Responsible · on site'],
      ['Ben Keller', 'Deputy'],
    ])
    expect(w.find('li[data-deputy="true"]').exists()).toBe(true)
  })

  it("names the responsible as the plan's settings do, unless the element has a label of its own", () => {
    const hero = show({ people: [anna, ben], as: 'responsible', closed: false, responsibleLabel: 'Hero' })
    expect(rows(hero)[0]).toEqual(['Anna Muster', 'Hero · on site'])
    const own = show(
      { people: [anna], as: 'responsible', closed: false, responsibleLabel: 'Hero' },
      { dutyLabel: 'Pikett' },
    )
    expect(rows(own)[0]).toEqual(['Anna Muster', 'Pikett · on site'])
    const fallback = show({
      people: [{ ...ben, deputy: false }],
      as: 'fallback',
      closed: false,
      responsibleLabel: 'Hero',
    })
    expect(rows(fallback)[0]).toEqual(['Ben Keller', 'Fallback'])
  })

  it('takes its own labels, and leaves them out where asked', () => {
    const own = show(
      { people: [anna, ben], as: 'responsible', closed: false },
      { dutyLabel: 'Pikett', deputyLabel: 'Stellvertretung' },
    )
    expect(rows(own)).toEqual([
      ['Anna Muster', 'Pikett · on site'],
      ['Ben Keller', 'Stellvertretung'],
    ])
    const bare = show({ people: [ben], as: 'fallback', closed: false }, { role: false })
    expect(bare.text()).toBe('Ben Keller')
  })

  it('shows the labels alone when the board hides names', () => {
    const w = show({ people: [{ ...anna, name: null }], as: 'responsible', closed: false }, { role: false })
    expect(w.text()).toBe('Responsible · on site')
  })

  it('says nobody, or closed, in its own words', () => {
    expect(show({ people: [], as: 'nobody', closed: false }).text()).toBe('Nobody is on duty today.')
    expect(show({ people: [], as: 'nobody', closed: true }).text()).toBe('Closed today.')
    expect(show({ people: [], as: 'nobody', closed: true }, { closedText: 'Wochenende' }).text()).toBe('Wochenende')
  })

  it('shows one line, the comment and the next open day', () => {
    const tomorrow = new Date(Date.now() + 86_400_000)
    const day = boardTime('Europe/Zurich', { dateFormat: 'Iso' }).date(tomorrow)
    const w = show(
      {
        people: [anna, ben],
        as: 'responsible',
        closed: false,
        comment: 'Server room closed',
        next: { day, people: ['Ben Keller'] },
      },
      { oneLine: true },
    )
    expect(w.findAll('.line > span').map((x) => x.text())).toEqual([
      'Anna Muster',
      'Responsible · on site',
      '·',
      'Ben Keller',
      'Deputy',
    ])
    expect(w.text()).toContain('“Server room closed”')
    expect(w.text()).toContain('Tomorrow: Ben Keller')
  })

  it('starts with one deputy when new', () => {
    expect(newElement('OnDuty').settings.deputies).toBe(1)
    expect(newElement('Clock').settings.deputies).toBeNull()
  })
})
