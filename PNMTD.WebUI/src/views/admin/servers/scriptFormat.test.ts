import { describe, expect, it } from 'vitest'
import { numericParameter, parameterName, runAccount, runnable } from './scriptFormat'

describe('scripts', () => {
  it('runs PowerShell once parsed, bash unless its header failed', () => {
    expect(runnable({ language: 'PowerShell', parseState: 'Pending' })).toBe(false)
    expect(runnable({ language: 'PowerShell', parseState: 'Parsed' })).toBe(true)
    expect(runnable({ language: 'Bash', parseState: 'Pending' })).toBe(true)
    expect(runnable({ language: 'Bash', parseState: 'Failed' })).toBe(false)
  })

  it('names parameters as each language uses them', () => {
    expect(parameterName('Name', 'PowerShell')).toBe('$Name')
    expect(parameterName('keep_days', 'Bash')).toBe('$PNMT_KEEP_DAYS')
  })

  it("knows both languages' numbers", () => {
    expect(numericParameter({ type: 'Int32' })).toBe(true)
    expect(numericParameter({ type: 'int' })).toBe(true)
    expect(numericParameter({ type: 'number' })).toBe(true)
    expect(numericParameter({ type: 'string' })).toBe(false)
  })

  it("says root for Linux's service and LocalSystem for Windows'", () => {
    expect(runAccount({ account: null, runAsKind: 'Service', runAsName: 'root (the service)' })).toBe('root')
    expect(runAccount({ account: null, runAsKind: 'Service', runAsName: 'System' })).toBe('LocalSystem')
    expect(runAccount({ account: 'deploy', runAsKind: 'User', runAsName: 'deploy' })).toBe('deploy')
  })
})
