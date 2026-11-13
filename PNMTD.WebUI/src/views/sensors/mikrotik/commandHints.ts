/**
 * What the command editor points out (docs/mikrotik-plan.md §8.2), before anything runs: a text that touches
 * PNMT's own objects can cut PNMT off, and some commands never end or take the router down with the session.
 * Hints only: PNMTD sends the text as typed, and the router enforces the user's rights.
 */
export interface CommandHint {
  severity: 'warn' | 'info'
  text: string
}

export function commandHints(text: string): CommandHint[] {
  const t = text.toLowerCase()
  const hints: CommandHint[] = []
  if (/pnmt/.test(t)) {
    hints.push({
      severity: 'warn',
      text: "The text mentions pnmt: changing PNMT's interface, user, group or firewall rule cuts off its access until the join script is pasted again.",
    })
  }
  if (/reset-configuration/.test(t)) {
    hints.push({ severity: 'warn', text: "A reset removes PNMT's access with everything else." })
  }
  if (/(^|[\s/{;])ping\s/.test(t) && !/count=/.test(t)) {
    hints.push({ severity: 'info', text: 'Ping without count= runs until the timeout: add count=4.' })
  }
  if (/traceroute/.test(t) && !/count=/.test(t)) {
    hints.push({ severity: 'info', text: 'Traceroute without count= runs until the timeout: add count=1.' })
  }
  if (/monitor(-traffic)?\b/.test(t) && !/\bonce\b/.test(t) && !/duration=/.test(t)) {
    hints.push({ severity: 'info', text: 'A monitor without once runs until the timeout: add once.' })
  }
  if (/\/tool\s+(torch|sniffer|bandwidth-test)/.test(t) && !/duration=/.test(t)) {
    hints.push({ severity: 'info', text: 'This tool runs until the timeout: add duration=10s.' })
  }
  if (/\/system\s+(reboot|shutdown)/.test(t) && !/:execute/.test(t)) {
    hints.push({
      severity: 'info',
      text: 'Use :execute {/system reboot}: the command then ends before the router goes down, instead of failing with the lost session.',
    })
  }
  return hints
}
