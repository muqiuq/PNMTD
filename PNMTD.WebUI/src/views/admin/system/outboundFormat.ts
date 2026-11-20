import type { S } from '@/api/resources'
import { prettyJson } from '@/components/jsonTree/jsonTree'
import { formatDuration } from '@/format'
import { formatBytes } from '@/format/bytes'

/** The outbound logs' words and numbers (docs/redmine-queue-outbound-plan.md §7.2, §7.3). */

export type OutboundLogName = 'notifications' | 'redmine'

/** What a masked value starts with (§6.5): "•••" and the last 4 characters of a key or token. */
export const MASKED = '•••'

export const isMasked = (value: string | null | undefined) => !!value && value.includes(MASKED)

/** "340 ms", "1.2 s", "15 s"; beyond a minute as formatDuration ("2 min"). */
export function formatMs(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return '–'
  const n = Math.max(0, Math.round(ms))
  if (n < 1000) return `${n} ms`
  if (n < 60_000) return `${(n / 1000).toFixed(n < 10_000 ? 1 : 0)} s`
  return formatDuration(n)
}

/** A body as shown: JSON indented (numbers as written), anything else as it is. */
export function bodyView(body: string | null | undefined): { text: string; json: boolean } {
  if (!body) return { text: '', json: false }
  try {
    JSON.parse(body.replace(/^\uFEFF/, ''))
  } catch {
    return { text: body, json: false }
  }
  return { text: prettyJson(body), json: true }
}

/** "256 KiB of 1.20 MiB stored" for a body cut at Outbound__MaxBodyKiB (§6.2); null when it is whole. */
export function storedNote(body: string | null | undefined, total: number, truncated: boolean): string | null {
  if (!truncated) return null
  const stored = new TextEncoder().encode(body ?? '').length
  return stored < total ? `${formatBytes(stored)} of ${formatBytes(total)} stored` : null
}

/** "redmine-1234-response.json": what Download names a body. An e-mail's request is its MIME message. */
export function downloadName(
  log: OutboundLogName,
  id: number,
  part: 'request' | 'response' | 'transcript',
  json: boolean,
) {
  const extension = json ? 'json' : log === 'notifications' && part === 'request' ? 'eml' : 'txt'
  return `${log === 'redmine' ? 'redmine' : 'notification'}-${id}-${part}.${extension}`
}

/** The queue line (§7.2): "Redmine: 1 button · 0 writes · 1 interactive · 3 background queued · 2 running". */
export function queueLine(q: S['RedmineQueueState']): string {
  const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`
  return (
    `Redmine: ${plural(q.button, 'button', 'buttons')} · ${plural(q.write, 'write', 'writes')} · ` +
    `${q.interactive} interactive · ${q.background} background queued · ${q.running} running`
  )
}

export const outcomeSeverity = (o: S['OutboundOutcome']): 'success' | 'danger' | 'info' | 'secondary' =>
  o === 'Ok' ? 'success' : o === 'Failed' ? 'danger' : o === 'Simulated' ? 'info' : 'secondary'

const channels: Record<string, { icon: string; label: string }> = {
  Email: { icon: 'pi pi-envelope', label: 'E-mail' },
  Pushover: { icon: 'pi pi-mobile', label: 'Pushover' },
  Http: { icon: 'pi pi-globe', label: 'HTTP' },
  Redmine: { icon: 'pi pi-ticket', label: 'Redmine' },
}

export const channelIcon = (channel: string) => channels[channel]?.icon ?? 'pi pi-send'
export const channelLabel = (channel: string) => channels[channel]?.label ?? channel

/** A lane as the queue line says it (§3.3): the refresh buttons' lane first. */
export const laneLabel = (lane: S['RedmineLane']) => (lane === 'Button' ? 'button' : lane.toLowerCase())
