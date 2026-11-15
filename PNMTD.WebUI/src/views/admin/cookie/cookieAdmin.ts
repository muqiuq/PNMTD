import type { S } from '@/api/resources'

/** Helpers of Admin → Cookie (docs/cookie-plan.md §9.3) that hold no state. */

export interface Option<T = string> {
  label: string
  value: T
}

/**
 * A new id for a row (a narrower instruction, a count column), as a version-4 UUID. crypto.getRandomValues works on plain
 * HTTP too, where crypto.randomUUID doesn't.
 */
export function newId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16))
  bytes[6] = (bytes[6] & 0x0f) | 0x40
  bytes[8] = (bytes[8] & 0x3f) | 0x80
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

/**
 * The options, plus one for each chosen value they don't list (a deleted user, or a list the reader may not load), so a
 * picker still shows what is chosen.
 */
export function withUnknown<T extends string | number>(
  options: Option<T>[],
  chosen: readonly (T | null | undefined)[],
  label: (value: T) => string,
): Option<T>[] {
  const known = new Set(options.map((o) => o.value))
  const extra = [...new Set(chosen)]
    .filter((v): v is T => v !== null && v !== undefined && !known.has(v))
    .map((value) => ({ label: label(value), value }))
  return [...options, ...extra]
}

/** The placeholders a prompt's text has lost: code fills them in, so they may move but not go (docs/cookie-prompts.md). */
export const missingPlaceholders = (text: string, required: readonly string[]) =>
  required.filter((p) => !text.includes(p))

/**
 * A prompt's or a kind of case's text into the overrides: only what differs from the default is kept, so a text edited
 * back to the default is a default again and follows a new release's.
 */
export function setOverride(overrides: Record<string, string>, key: string, text: string, fallback: string) {
  if (text === fallback) {
    delete overrides[key]
  } else {
    overrides[key] = text
  }
}

/** Cookie's actions with a level (docs/cookie-plan.md §6, §21.1), by the names the settings use. */
export const actionKeys = [
  'pushover',
  'email',
  'ticket',
  'privateNote',
  'publicAnswer',
  'objectNote',
  'mailEvent',
] as const

export type ActionKey = (typeof actionKeys)[number]

/**
 * The defaults of §13.4 and §21.1: what leaves PNMT or reaches people at Propose, a note on a host or sensor and a mail
 * rule's event at Auto; every threshold 0.7.
 */
export const actionDefaults: Record<ActionKey, S['ActionRule']> = {
  pushover: { level: 'Propose', minConfidence: 0.7 },
  email: { level: 'Propose', minConfidence: 0.7 },
  ticket: { level: 'Propose', minConfidence: 0.7 },
  privateNote: { level: 'Propose', minConfidence: 0.7 },
  publicAnswer: { level: 'Propose', minConfidence: 0.7 },
  objectNote: { level: 'Auto', minConfidence: 0.7 },
  mailEvent: { level: 'Auto', minConfidence: 0.7 },
}

/** Every action with a rule, the defaults for any the settings lack. */
export function normalizeLevels(levels: S['ActionLevels'] | undefined): Required<S['ActionLevels']> {
  return Object.fromEntries(
    actionKeys.map((k) => [
      k,
      {
        level: levels?.[k]?.level ?? actionDefaults[k].level,
        minConfidence: levels?.[k]?.minConfidence ?? actionDefaults[k].minConfidence,
      },
    ]),
  ) as Required<S['ActionLevels']>
}
