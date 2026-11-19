import { describe, expect, it } from 'vitest'
import type { S } from '@/api/resources'
import {
  anyAddress,
  duration,
  examples,
  networkHint,
  outcomeLabel,
  refusedLabel,
  sampleArguments,
  slugOf,
  stateSeverity,
  targetSentence,
} from './connectorFormat'

describe('connectorFormat', () => {
  it('says what became of a call, and colours it (docs/connectors-plan.md §13.2)', () => {
    expect(outcomeLabel({ state: 'Refused', refusedBy: 'address_refused', dryRun: false })).toBe('refused · networks')
    expect(outcomeLabel({ state: 'Checked', refusedBy: 'review', dryRun: true })).toBe(
      'checked · would be refused: review',
    )
    expect(outcomeLabel({ state: 'TimedOut', refusedBy: null, dryRun: false })).toBe('timed out')
    expect(refusedLabel('something_new')).toBe('something new')
    expect(stateSeverity('Done', 0)).toBe('success')
    expect(stateSeverity('Done', 2)).toBe('warn')
    expect(stateSeverity('Queued')).toBe('info')
    expect(stateSeverity('Lost')).toBe('danger')
    expect(stateSeverity('Expired')).toBe('warn')
  })

  it('hints at a network as it is typed (§6.3)', () => {
    expect(networkHint('10.20.0.17')).toBe('One address: saved as 10.20.0.17/32.')
    expect(networkHint('10.20.0.17/24')).toBe(
      'A network starts at its first address: 10.20.0.0/24, or 10.20.0.17/32 for one address.',
    )
    expect(networkHint('10.20.0.0/24')).toBeNull()
    expect(networkHint('2001:db8::17')).toBe('One address: saved as 2001:db8::17/128.')
    expect(networkHint('0.0.0.0/0')).toBe('Any IPv4 address.')
    expect(networkHint('10.0.0.300')).toBe('Each part of an IPv4 address is 0 to 255.')
    expect(networkHint('web01')).toMatch(/^Not an address/)
    expect(networkHint('')).toBeNull()
    expect(anyAddress([{ cidr: '10.0.0.0/8' }, { cidr: '::/0' }])).toBe(true)
    expect(anyAddress([{ cidr: '10.0.0.0/8' }])).toBe(false)
  })

  it('writes the target as a sentence and suggests a slug (§13.3, §13.5)', () => {
    expect(
      targetSentence({
        server: 'WEB01 / Windows',
        scriptName: 'Shop-Tools.ps1',
        function: 'Clear-ShopCache',
        runAsKind: 'Stored',
        account: 'svc-shop',
        linux: false,
      }),
    ).toBe('Runs Clear-ShopCache in Shop-Tools.ps1 on WEB01 / Windows as svc-shop')
    expect(targetSentence({ server: 'backup01', scriptName: 'backup.sh', runAsKind: 'Service', linux: true })).toBe(
      'Runs backup.sh on backup01 as root (the service)',
    )
    expect(slugOf('Shop: flush cache')).toBe('shop-flush-cache')
    expect(slugOf('Zürich – Backup ')).toBe('zurich-backup')
  })

  it('times queues and runs for people', () => {
    expect(duration(0.85)).toBe('850 ms')
    expect(duration(4.25)).toBe('4.3 s')
    expect(duration(125)).toBe('2 min 05 s')
    expect(duration(3780)).toBe('1 h 03 min')
    expect(duration(null)).toBe('')
  })

  it('builds the Usage examples with the key as $PNMT_KEY, never filled in (§13.5)', () => {
    const argument = (a: Partial<S['ConnectorDescribedArgument']>): S['ConnectorDescribedArgument'] => ({
      name: 'x',
      type: 'string',
      required: false,
      pattern: null,
      help: null,
      default: null,
      oneOf: null,
      min: null,
      max: null,
      multiline: false,
      ...a,
    })
    const args = sampleArguments([
      argument({ name: 'Site', required: true }),
      argument({ name: 'Scope', oneOf: ['Pages', 'All'] }),
      argument({ name: 'Days', type: 'number', required: true, min: 1, max: 30 }),
      argument({ name: 'Note', multiline: true }),
    ])
    expect(args).toEqual({ Site: '<Site>', Scope: 'Pages', Days: 1 })
    const [curl, ps, py] = examples('https://pnmt.example.com/connector/v1/shop', args, true)
    expect(curl.code).toContain('-H "Authorization: Bearer $PNMT_KEY"')
    expect(curl.code).toContain('/tickets/$ticket')
    expect(ps.code).toContain("Site = '<Site>'; Scope = 'Pages'; Days = 1")
    expect(py.code).toContain("os.environ['PNMT_KEY']")
  })
})
