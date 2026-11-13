import type { S } from '@/api/resources'

/** Labels and colours for diagnostics (docs/windows-sensor-plan.md §10.3). */

export const outcomeSeverity = (o: S['DiagnosticOutcome'] | null | undefined) =>
  o === 'Pass' ? 'success' : o === 'Fail' ? 'danger' : o === 'Error' ? 'warn' : 'secondary'

export const outcomeLabel = (o: S['DiagnosticOutcome'] | null | undefined) => (o ? o.toLowerCase() : 'waiting')

export const schedule = (a: Pick<S['DiagnosticAssignmentItem'], 'mode' | 'intervalMinutes' | 'enabled'>) =>
  `${a.mode === 'Scheduled' ? `every ${a.intervalMinutes} min` : 'by hand'}${a.enabled ? '' : ' (off)'}`

/** One line for a check's parameters: "ping 10.0.1.1 ×4, 1000 ms" or "dns intranet.example A @10.0.0.53". */
export function checkLine(c: {
  type: S['DiagnosticCheckType']
  ping?: S['PingParameters'] | null
  dns?: S['DnsParameters'] | null
}): string {
  if (c.type === 'Ping' && c.ping) {
    const limits = [
      c.ping.maxLossPercent ? `loss ≤ ${c.ping.maxLossPercent} %` : '',
      c.ping.maxAvgMs ? `avg ≤ ${c.ping.maxAvgMs} ms` : '',
    ]
      .filter(Boolean)
      .join(', ')
    return `ping ${c.ping.target} ×${c.ping.count}, ${c.ping.timeoutMs} ms${c.ping.family !== 'Any' ? `, ${c.ping.family}` : ''}${limits ? `; ${limits}` : ''}`
  }
  if (c.dns) {
    return `dns ${c.dns.name} ${c.dns.recordType}${c.dns.server ? ` @${c.dns.server}` : ''}${c.dns.expect ? ` = ${c.dns.expect}` : ''}${c.dns.maxMs ? `, ≤ ${c.dns.maxMs} ms` : ''}`
  }
  return c.type
}
