import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { newElement } from '../layout'
import type { BoardElementData, BoardElementSettings } from '../types'
import SensorMessage from './SensorMessage.vue'

/** docs/board-messages-plan.md §9.3: the texts for no message, no data and a deleted sensor; Fit grows a message, never those. */
function show(
  sensorMessage: BoardElementData['sensorMessage'],
  settings: Partial<BoardElementSettings> = {},
  height: number | null = null,
) {
  const element = { ...newElement('SensorMessage'), height }
  element.settings = { ...element.settings, ...settings }
  const data: BoardElementData = { id: element.id, type: 'SensorMessage', state: 'Ok', more: 0, sensorMessage }
  return mount(SensorMessage, { props: { element, data, fit: true, now: Date.now() } })
}

const sensor = { sensorId: 's', host: 'fw-01', name: 'temperature', at: new Date().toISOString() }

describe('Sensor message', () => {
  it('says what there is instead of a message', () => {
    expect(show({ ...sensor, state: 'Ok', message: null }).text()).toContain('No message')
    expect(show({ ...sensor, state: 'NoData', message: null }).text()).toContain('No data yet')
    expect(show({ sensorId: 's', host: null, name: null, at: null, state: 'Missing', message: null }).text()).toBe(
      'This sensor no longer exists',
    )
  })

  it('keeps a placeholder at the medium size under Fit, and fits a message', () => {
    const empty = show({ ...sensor, state: 'NoData', message: null }, { textSize: 'Fit' })
    expect(empty.find('[data-test="sensor-message"]').attributes('style')).toContain('font-size: 1.5em')
    expect(empty.find('.sensor-message').attributes('data-fit')).toBeUndefined()
    // A share of the column's height: the box; its own height: the width only.
    const value = show({ ...sensor, state: 'Ok', message: '41.5 °C' }, { textSize: 'Fit' }, 1)
    expect(value.find('.sensor-message').attributes('data-fit')).toBe('box')
    const own = show({ ...sensor, state: 'Ok', message: '41.5 °C' }, { textSize: 'Fit' })
    expect(own.find('.sensor-message').attributes('data-fit')).toBe('width')
  })

  it('keeps line breaks, and colours by the state', () => {
    const w = show(
      { ...sensor, state: 'Failing', message: 'Queue: 12 mails waiting,\noldest 3 min' },
      { monospace: true },
    )
    expect(w.find('[data-test="sensor-message"]').element.textContent).toBe('Queue: 12 mails waiting,\noldest 3 min')
    expect(w.find('.sensor-message').attributes('data-tone')).toBe('fail')
    expect(w.find('.text').classes()).toContain('mono')
  })
})
