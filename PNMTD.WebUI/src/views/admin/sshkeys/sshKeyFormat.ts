import type { S } from '@/api/resources'
import type { TagSeverity } from '@/views/admin/servers/agentFormat'

/** Labels and checks of the SSH key manager (docs/linux-sensor-plan.md §6.4, §6.5, §11.4). Pure, so they are tested. */

/** A key's type and size, short: "ed25519 256", "rsa 2048", "ecdsa-nistp256 256". */
export function keyType(type: string, bits: number | null | undefined): string {
  const short = type
    .replace(/^ssh-/, '')
    .replace('-sha2-', '-')
    .replace(/@openssh\.com$/, '')
  return bits ? `${short} ${bits}` : short
}

/** The register's notes (SshKeyService.Filters), in words, with their colour. */
export const keyNotes: Record<string, { label: string; severity: TagSeverity }> = {
  weak: { label: 'weak', severity: 'danger' },
  unused180: { label: 'unused for 180 days', severity: 'secondary' },
  ownerDeactivated: { label: 'owner deactivated', severity: 'warn' },
  retiredStillAuthorized: { label: 'retired, still authorized', severity: 'danger' },
}

export const noteLabel = (note: string) => keyNotes[note]?.label ?? note
export const noteSeverity = (note: string): TagSeverity => keyNotes[note]?.severity ?? 'secondary'

/** The register's filters, as the API names them. */
export const keyFilters = Object.entries(keyNotes).map(([value, n]) => ({ label: n.label, value }))

export const removalStateLabel = (s: S['SshKeyRemovalState']) =>
  s === 'NotFound' ? 'not found' : s === 'Removed' ? 'removed' : s.toLowerCase()

export const removalStateSeverity = (s: S['SshKeyRemovalState']): TagSeverity =>
  s === 'Removed'
    ? 'success'
    : s === 'Pending' || s === 'Sent'
      ? 'info'
      : s === 'NotFound' || s === 'Expired'
        ? 'secondary'
        : s === 'Refused'
          ? 'warn'
          : 'danger'

export const removalStates: S['SshKeyRemovalState'][] = [
  'Pending',
  'Sent',
  'Removed',
  'NotFound',
  'Refused',
  'Failed',
  'Expired',
]

/** Why a removal was asked for (§11.4's Removals tab). */
export const reasonLabel = (r: S['SshKeyRemovalReason']) =>
  ({
    Server: "from a server's tab",
    Key: 'a key from its servers',
    Retirement: 'a retired key',
    Offboarding: 'offboarding',
    Unregistered: 'an unregistered key',
  })[r]

export const removalReasons: S['SshKeyRemovalReason'][] = ['Server', 'Key', 'Retirement', 'Offboarding', 'Unregistered']

/** What the remove dialog sends for each place a key is authorized. */
export const targetsOf = (
  places: Pick<S['SshKeyAuthorizationItem'], 'server' | 'account' | 'path' | 'fingerprint'>[],
) => places.map((a) => ({ sensorId: a.server.id, account: a.account, path: a.path, fingerprint: a.fingerprint }))

/** A pasted line as Add keys… shows it: fine, or refused with why (a blank line is no line). */
export function parseLineState(
  l: S['SshKeyParseLine'],
): { ok: boolean; label: string | null; severity: TagSeverity } | null {
  if (l.problem === 'Blank') return null
  if (l.problem === 'PrivateKey') return { ok: false, label: 'a private key: refused', severity: 'danger' }
  if (l.problem !== 'None') return { ok: false, label: l.problemText ?? 'not a public key', severity: 'danger' }
  if (l.registered) return { ok: false, label: `registered: ${l.registered.name}`, severity: 'warn' }
  if (l.duplicate) return { ok: false, label: 'twice in the text', severity: 'warn' }
  return { ok: true, label: null, severity: 'success' }
}

/** Whether the pasted keys can be registered: at least one key, nothing refused, no private key. */
export const canRegister = (p: S['SshKeyParseResult'] | null | undefined) =>
  !!p && p.keys > 0 && p.refused === 0 && !p.privateKey

/**
 * The names to send, in the keys' order (the parse's index): each key's typed name, else its suggested name, else a name from
 * the line; null for none, so the server's template applies.
 */
export function namesByIndex(lines: S['SshKeyParseLine'][], typed: Record<number, string>): string[] {
  return lines
    .filter((l) => l.index !== null && l.index !== undefined)
    .sort((a, b) => a.index! - b.index!)
    .map(
      (l) => (typed[l.line] ?? l.suggestedName ?? '').trim() || `${keyType(l.type ?? 'key', l.bits)} (line ${l.line})`,
    )
}

/** The typed confirmation: the number of keys, as the dialog asks for it. */
export const confirmMatches = (typed: string | number | null | undefined, confirm: number) =>
  confirm > 0 && String(typed ?? '').trim() === String(confirm)

/** The owner as a line: the user (or "former user …") or the text. */
export const ownerName = (o: S['SshKeyOwner']) => o.name

/** What to remove: chosen places (a server's tab, the Unregistered list), every place of a key, or a user's keys (offboarding). */
export type RemovalSource =
  | { kind: 'items'; items: S['SshKeyRemovalTarget'][]; reason: S['SshKeyRemovalReason']; title?: string }
  | { kind: 'key'; keyId: string; name: string; reason: S['SshKeyRemovalReason'] }
  | { kind: 'user'; userId: string; name: string }
