import type { S } from '@/api/resources'

/** An Advanced sensor with the rules of docs/advanced-sensor-plan.md §6.7, for the Rules tab's tests. */
export const SENSOR_ID = '5b0c1d2e-3f40-4a5b-8c6d-7e8f90a1b2c3'
export const RULE_ID = 'a1a1a1a1-0000-4000-8000-000000000001'
export const OTHER_RULE_ID = 'a1a1a1a1-0000-4000-8000-000000000002'
export const REPORT_ID = 'b2b2b2b2-0000-4000-8000-000000000001'

export function result(patch: Partial<S['AdvancedRuleResult']> = {}): S['AdvancedRuleResult'] {
  return {
    ruleId: RULE_ID,
    name: 'Disk space',
    path: 'disk.free_gb',
    condition: 'disk.free_gb ≥ 5',
    severity: 'Fail',
    outcome: 'Fail',
    reason: null,
    detail: 'must be at least 5',
    actual: [{ path: 'disk.free_gb', value: '3.2' }],
    matches: ['disk.free_gb'],
    matchCount: 1,
    assumedUtc: false,
    ...patch,
  }
}

export function rule(patch: Partial<S['AdvancedRuleItem']> = {}): S['AdvancedRuleItem'] {
  return {
    id: RULE_ID,
    order: 0,
    name: 'Disk space',
    enabled: true,
    note: null,
    path: 'disk.free_gb',
    operator: 'Ge',
    operand: '5',
    operand2: null,
    ignoreCase: false,
    quantifier: 'All',
    whenMissing: 'Fail',
    severity: 'Fail',
    condition: 'disk.free_gb ≥ 5',
    hasWildcard: false,
    last: result(),
    failingSince: '2026-11-17T08:00:00Z',
    ...patch,
  }
}

export function advanced(patch: Partial<S['AdvancedSensorDetail']> = {}): S['AdvancedSensorDetail'] {
  return {
    namesPeople: false,
    canSeeData: true,
    lastReportAt: '2026-11-17T10:00:00Z',
    lastValidReportAt: '2026-11-17T10:00:00Z',
    lastSource: '10.0.0.5',
    stale: false,
    staleAt: null,
    verdict: {
      code: 495,
      message: '1 rule fails: Disk space (disk.free_gb = 3.2, must be at least 5)',
      failed: 1,
      warnings: 0,
      evaluatedAt: '2026-11-17T10:00:01Z',
    },
    rules: [rule()],
    fields: [],
    latest: {
      id: REPORT_ID,
      receivedAt: '2026-11-17T10:00:00Z',
      lastReceivedAt: '2026-11-17T10:00:00Z',
      repeats: 0,
      source: '10.0.0.5',
      bytes: 412,
      valid: true,
      errorKind: null,
      error: null,
      status: 'ok',
      message: 'Nightly backup done',
      measuredAt: null,
      clockSkewSeconds: null,
      evaluated: true,
      code: 495,
    },
    pending: 0,
    reportCount: 1,
    ...patch,
  }
}

export function sensor(a: S['AdvancedSensorDetail'] = advanced()): S['SensorDetail'] {
  return {
    id: SENSOR_ID,
    host: { id: 'c3c3c3c3-0000-4000-8000-000000000001', name: 'app-01' },
    parentSensorId: null,
    name: 'Backup app',
    type: 'ADVANCED',
    enabled: true,
    ignore: false,
    interval: 300,
    gracePeriod: 60,
    parameters: null,
    textId: null,
    status: null,
    created: '2026-11-01T00:00:00Z',
    state: 'Failing',
    lastCode: 495,
    lastMessage: null,
    lastEventAt: null,
    failingSince: null,
    uptime24Hours: 1,
    uptime30Days: 1,
    days: { start: '2026-10-18', values: [] },
    children: [],
    integration: null,
    advanced: a,
  }
}

/** The latest report's paths (GET paths), as AdvancedPath.Enumerate gives them. */
export const PATHS: S['AdvancedPathItem'][] = [
  { path: 'status', kind: 'string', preview: '"ok"' },
  { path: 'disk', kind: 'object', preview: null },
  { path: 'disk.free_gb', kind: 'number', preview: '3.2' },
  { path: 'backup.verified', kind: 'true', preview: 'true' },
  { path: 'jobs', kind: 'array', preview: null },
  { path: 'jobs[0].state', kind: 'string', preview: '"done"' },
  { path: 'jobs[*].state', kind: 'string', preview: null },
]

/** A test's answer (POST test) with one rule's outcome. */
export function tested(results: S['AdvancedRuleResult'][], patch: Partial<S['AdvancedTestResult']> = {}) {
  return {
    code: results.some((r) => r.outcome === 'Fail') ? 495 : results.some((r) => r.outcome === 'Warn') ? 296 : 295,
    state: results.some((r) => r.outcome === 'Fail') ? 'failing' : 'ok',
    message: null,
    results,
    fields: [],
    error: null,
    reportId: REPORT_ID,
    ...patch,
  } satisfies S['AdvancedTestResult']
}
