import { describe, expect, it } from 'vitest'
import {
  bodyView,
  channelLabel,
  downloadName,
  formatMs,
  isMasked,
  laneLabel,
  outcomeSeverity,
  queueLine,
  storedNote,
} from './outboundFormat'

describe('outboundFormat', () => {
  it('shows a JSON body indented, with its numbers as written, and anything else as it is (§7.3)', () => {
    expect(bodyView('{"issue":{"id":603,"done_ratio":3.20}}')).toEqual({
      text: '{\n  "issue": {\n    "id": 603,\n    "done_ratio": 3.20\n  }\n}',
      json: true,
    })
    expect(bodyView('Subject: Disk full\r\n\r\nC: is at 98 %')).toEqual({
      text: 'Subject: Disk full\r\n\r\nC: is at 98 %',
      json: false,
    })
    // A body cut at the cap isn't JSON any more: shown as stored.
    expect(bodyView('{"issues":[{"id":1},')).toEqual({ text: '{"issues":[{"id":1},', json: false })
    expect(bodyView(null)).toEqual({ text: '', json: false })
  })

  it('says how much of a cut body is stored, and nothing for a whole one (§6.2)', () => {
    expect(storedNote('x'.repeat(256 * 1024), 1.2 * 1024 * 1024, true)).toBe('256 KiB of 1.20 MiB stored')
    expect(storedNote('{}', 2, true)).toBeNull()
    expect(storedNote('x'.repeat(10), 5000, false)).toBeNull()
    // Bytes, not characters: "•" is three bytes in UTF-8.
    expect(storedNote('•••', 2048, true)).toBe('9 B of 2.00 KiB stored')
  })

  it('writes durations, masks and names', () => {
    expect(formatMs(0)).toBe('0 ms')
    expect(formatMs(340.4)).toBe('340 ms')
    expect(formatMs(1234)).toBe('1.2 s')
    expect(formatMs(15_400)).toBe('15 s')
    expect(formatMs(125_000)).toBe('2 min')
    expect(formatMs(null)).toBe('–')
    expect(isMasked('•••6789')).toBe(true)
    expect(isMasked('pushover://•••abcd')).toBe(true)
    expect(isMasked('application/json')).toBe(false)
    expect(channelLabel('Http')).toBe('HTTP')
    expect(laneLabel('Button')).toBe('button')
    expect(laneLabel('Background')).toBe('background')
    expect(outcomeSeverity('Failed')).toBe('danger')
    expect(outcomeSeverity('Simulated')).toBe('info')
    expect(downloadName('redmine', 12, 'response', true)).toBe('redmine-12-response.json')
    expect(downloadName('notifications', 7, 'request', false)).toBe('notification-7-request.eml')
    expect(downloadName('notifications', 7, 'transcript', false)).toBe('notification-7-transcript.txt')
  })

  it('counts the Redmine queue per lane (§7.2)', () => {
    const queue = {
      button: 1,
      write: 0,
      interactive: 1,
      background: 3,
      running: 2,
      slotsInUse: 2,
      slots: 4,
      backgroundPausedUntil: null,
      jobs: [],
    }
    expect(queueLine(queue)).toBe('Redmine: 1 button · 0 writes · 1 interactive · 3 background queued · 2 running')
    expect(queueLine({ ...queue, button: 0, write: 1 })).toContain('0 buttons · 1 write ·')
  })
})
