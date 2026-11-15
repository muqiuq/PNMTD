import { describe, expect, it } from 'vitest'
import { updateHow } from './agentFormat'

describe('how an agent updates', () => {
  it('runs its line on Linux, and on Windows from 1.8; before 1.8 a Windows agent runs the new executable once', () => {
    expect(updateHow('Linux', '1.3.0')).toBe('linux')
    expect(updateHow('Windows', '1.8.0')).toBe('windows')
    expect(updateHow('Windows', '1.8.0-rc1')).toBe('windows')
    expect(updateHow('Windows', '2.0.1')).toBe('windows')
    expect(updateHow('Windows', '1.7.0')).toBe('windows-legacy')
    expect(updateHow('Windows', null)).toBe('windows-legacy')
    expect(updateHow('Windows', 'dev')).toBe('windows-legacy')
  })
})
