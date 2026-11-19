import type { S } from '@/api/resources'

/** 3rd Party Connectors' words for people (docs/connectors-plan.md §13): states, refusals, waits, types and the networks' hints. */

export type CallState = S['ConnectorCallState']

/** What a script's selector takes (§3.3): a copy of the text, where it came from, and a registered script's account. */
export interface TakenScript {
  name: string
  text: string
  takenFrom: string
  runAsKind?: S['RunAsKind']
  account?: string | null
}
type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast'

const stateLabels: Record<CallState, string> = {
  Queued: 'queued',
  Running: 'running',
  Done: 'done',
  Failed: 'failed',
  TimedOut: 'timed out',
  Offline: 'offline',
  Lost: 'lost',
  Refused: 'refused',
  Expired: 'expired',
  Cancelled: 'cancelled',
  Checked: 'checked',
}

export function stateLabel(state: CallState): string {
  return stateLabels[state] ?? state
}

/** The tag's colour: green done, red what failed or was refused, amber what didn't happen, blue what is on its way. */
export function stateSeverity(state: CallState, exitCode?: number | null): Severity {
  switch (state) {
    case 'Done':
      return exitCode ? 'warn' : 'success'
    case 'Checked':
      return 'success'
    case 'Queued':
    case 'Running':
      return 'info'
    case 'Failed':
    case 'TimedOut':
    case 'Lost':
    case 'Refused':
      return 'danger'
    default:
      return 'warn'
  }
}

const refusals: Record<string, string> = {
  address_refused: 'networks',
  caller_disabled: 'caller off',
  unknown_connector: 'not granted',
  connector_disabled: 'connector off',
  invalid_body: 'body',
  too_large: 'too large',
  arguments_invalid: 'arguments',
  arguments_need_review: 'needs an admin',
  rate_limited: 'calls per hour',
  queue_full: 'queue full',
  review: 'review',
  review_unavailable: 'review unavailable',
  server_not_found: 'no server',
  server_ambiguous: 'several servers',
  server_offline: 'server offline',
  script_invalid: 'script',
  agent_too_old: 'agent too old',
  agent: 'agent',
}

/** What refused a call, in a word or two: "networks", "arguments", "review". */
export function refusedLabel(refusedBy: string | null | undefined): string | null {
  return refusedBy ? (refusals[refusedBy] ?? refusedBy.replaceAll('_', ' ')) : null
}

/** The state with what refused it: "refused · networks", or for a dry run "checked · would be refused: review". */
export function outcomeLabel(c: Pick<S['ConnectorCallItem'], 'state' | 'refusedBy' | 'dryRun'>): string {
  const by = refusedLabel(c.refusedBy)
  if (c.state === 'Checked') return by ? `checked · would be refused: ${by}` : 'checked'
  return by && c.state === 'Refused' ? `refused · ${by}` : stateLabel(c.state)
}

export function isActive(state: CallState): boolean {
  return state === 'Queued' || state === 'Running'
}

export function typeLabel(type: S['ConnectorType']): string {
  return type === 'LinuxScript' ? 'Linux Server Script' : 'Windows Server Script'
}

export function modeLabel(mode: S['ConnectorMode']): string {
  return mode === 'Ticket' ? 'ticket' : 'wait'
}

/** "2 min 05 s", "850 ms", "1 h 03 min". */
export function duration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined) return ''
  if (seconds < 1) return `${Math.round(seconds * 1000)} ms`
  if (seconds < 60) return `${seconds < 10 ? seconds.toFixed(1) : Math.round(seconds)} s`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes} min ${String(Math.round(seconds % 60)).padStart(2, '0')} s`
  return `${Math.floor(minutes / 60)} h ${String(minutes % 60).padStart(2, '0')} min`
}

/** A slug from a name, as the form suggests it: "Shop: flush cache" → "shop-flush-cache". */
export function slugOf(name: string): string {
  return name
    .toLowerCase()
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 63)
}

const ipv4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/

/** What the form says about a network as it is typed (§6.3), before the server normalises it. Null: fine. */
export function networkHint(entry: string): string | null {
  const text = entry.trim()
  if (!text) return null
  const [address, prefix] = text.split('/')
  if (!address) return 'An address, or a network such as 10.20.0.0/24.'
  const v4 = ipv4.exec(address)
  if (v4) {
    const parts = v4.slice(1).map(Number)
    if (parts.some((p) => p > 255)) return 'Each part of an IPv4 address is 0 to 255.'
    if (prefix === undefined) return `One address: saved as ${address}/32.`
    const bits = Number(prefix)
    if (!Number.isInteger(bits) || bits < 0 || bits > 32) return 'The prefix of an IPv4 network is 0 to 32.'
    const value = parts.reduce((a, p) => a * 256 + p, 0)
    const start = Math.floor(value / 2 ** (32 - bits)) * 2 ** (32 - bits)
    if (value !== start) {
      const first = [24, 16, 8, 0].map((s) => Math.floor(start / 2 ** s) % 256).join('.')
      return `A network starts at its first address: ${first}/${bits}, or ${address}/32 for one address.`
    }
    if (bits === 0) return 'Any IPv4 address.'
    return null
  }
  if (address.includes(':')) {
    if (prefix === undefined) return `One address: saved as ${address}/128.`
    const bits = Number(prefix)
    if (!Number.isInteger(bits) || bits < 0 || bits > 128) return 'The prefix of an IPv6 network is 0 to 128.'
    if (bits === 0) return 'Any IPv6 address.'
    return null
  }
  return 'Not an address: 10.20.0.17, 10.20.0.0/24 or 2001:db8::/48.'
}

/** Whether the networks let any address in: 0.0.0.0/0 or ::/0. */
export function anyAddress(networks: readonly { cidr: string }[]): boolean {
  return networks.some((n) => n.cidr.trim() === '0.0.0.0/0' || n.cidr.trim() === '::/0')
}

/** The target as a sentence (§13.5), from what the form holds before a save. */
export function targetSentence(t: {
  server: string
  scriptName: string
  function?: string | null
  runAsKind: S['RunAsKind']
  account?: string | null
  linux: boolean
}): string {
  const who =
    t.runAsKind === 'Service'
      ? t.linux
        ? 'root (the service)'
        : 'the service (LocalSystem)'
      : t.runAsKind === 'Gmsa'
        ? `${t.account} (gMSA)`
        : (t.account ?? '')
  const what = t.function ? `${t.function} in ${t.scriptName || 'the script'}` : t.scriptName || 'the script'
  return `Runs ${what} on ${t.server || 'a server'} as ${who}`
}

/** A JSON body's arguments for the examples: each exposed argument with a value of its type. */
export function sampleArguments(args: readonly S['ConnectorDescribedArgument'][]): Record<string, unknown> {
  const sample: Record<string, unknown> = {}
  for (const a of args) {
    if (!a.required && !a.oneOf?.length) continue
    sample[a.name] =
      a.type === 'boolean'
        ? false
        : a.type === 'number'
          ? Number(a.default ?? a.min ?? 1)
          : (a.oneOf?.[0] ?? a.default ?? `<${a.name}>`)
  }
  return sample
}

/** The three examples of the Usage tab (§13.5): curl, PowerShell and Python, with the key as $PNMT_KEY, never filled in. */
export function examples(
  url: string,
  args: Record<string, unknown>,
  ticket: boolean,
): { label: string; code: string }[] {
  const body = JSON.stringify({ arguments: args })
  const psArgs = Object.entries(args)
    .map(
      ([k, v]) =>
        `${k} = ${typeof v === 'string' ? `'${v.replaceAll("'", "''")}'` : typeof v === 'boolean' ? (v ? '$true' : '$false') : v}`,
    )
    .join('; ')
  const pyArgs = JSON.stringify(args).replaceAll('true', 'True').replaceAll('false', 'False')
  return [
    {
      label: 'curl',
      code:
        `curl -sS ${url} \\\n  -H "Authorization: Bearer $PNMT_KEY" -H "Content-Type: application/json" \\\n  -d '${body}'` +
        (ticket
          ? `\n\n# then, with the ticket from the answer:\ncurl -sS ${url}/tickets/$ticket -H "Authorization: Bearer $PNMT_KEY"`
          : ''),
    },
    {
      label: 'PowerShell',
      code: `$headers = @{ Authorization = "Bearer $env:PNMT_KEY" }\n$body = @{ arguments = @{ ${psArgs} } } | ConvertTo-Json\nInvoke-RestMethod -Method Post -Uri ${url} -Headers $headers -Body $body -ContentType 'application/json'`,
    },
    {
      label: 'Python',
      code: `import os, requests\nr = requests.post("${url}",\n                  headers={"Authorization": f"Bearer {os.environ['PNMT_KEY']}"},\n                  json={"arguments": ${pyArgs}}, timeout=120)\nprint(r.json()["state"])`,
    },
  ]
}
