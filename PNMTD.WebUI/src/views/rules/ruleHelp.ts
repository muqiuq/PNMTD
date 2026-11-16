import type { S } from '@/api/resources'

/** How an address is read (§5.6): by its form, as the providers decide on the server. */
export const recipientHint = 'An e-mail address, pushover://<user key>, or an http(s):// URL for a webhook.'

type Kind = S['NotificationRecipientKind']
type Hours = S['NotificationHours']

/**
 * Whom a rule tells (docs/notification-recipients-plan.md §7.1): an address as always, a user, a group, or the responsible
 * of the day. An address comes first and is the form's default, so that creating one is the same two fields as before.
 */
export const recipientKinds: { label: string; value: Kind }[] = [
  { label: 'An address', value: 'Address' },
  { label: 'A user', value: 'User' },
  { label: 'A group', value: 'Group' },
  { label: 'The responsible', value: 'Responsible' },
]

export const hoursOptions: { label: string; value: Hours }[] = [
  { label: 'At any hour', value: 'Always' },
  { label: 'In working hours only', value: 'WorkingHours' },
]

export type StandIn = 'nobody' | 'user' | 'group'

export const standInOptions: { label: string; value: StandIn }[] = [
  { label: 'Nobody', value: 'nobody' },
  { label: 'A user', value: 'user' },
  { label: 'A group', value: 'group' },
]

/** What the recipient block edits; the request is made from it with recipientRequest(). */
export interface RecipientDraft {
  recipientKind: Kind
  recipient: string
  userId: string | null
  groupId: string | null
  byEmail: boolean
  byPushover: boolean
  hours: Hours
  standIn: StandIn
}

export function recipientDraft(rule?: S['NotificationRuleDetail']): RecipientDraft {
  const kind = rule?.recipientKind ?? 'Address'
  const userId = rule?.user?.id ?? null
  const groupId = rule?.group?.id ?? null
  return {
    recipientKind: kind,
    recipient: kind === 'Address' ? (rule?.recipient ?? '') : '',
    userId,
    groupId,
    byEmail: rule?.byEmail ?? true,
    byPushover: rule?.byPushover ?? true,
    hours: rule?.hours ?? 'Always',
    standIn: kind === 'Responsible' ? (userId ? 'user' : groupId ? 'group' : 'nobody') : 'nobody',
  }
}

type RecipientFields = Pick<
  S['NotificationRuleRequest'],
  'recipientKind' | 'recipient' | 'userId' | 'groupId' | 'byEmail' | 'byPushover' | 'hours'
>

/** The request's recipient fields from the draft: only what the kind needs, the rest null or the default (§6.1). */
export function recipientRequest(d: RecipientDraft): RecipientFields {
  const kind = d.recipientKind
  const person = kind !== 'Address'
  return {
    recipientKind: kind,
    recipient: kind === 'Address' ? d.recipient.trim() : null,
    userId: kind === 'User' || (kind === 'Responsible' && d.standIn === 'user') ? d.userId : null,
    groupId: kind === 'Group' || (kind === 'Responsible' && d.standIn === 'group') ? d.groupId : null,
    byEmail: person ? d.byEmail : true,
    byPushover: person ? d.byPushover : true,
    hours: kind === 'Responsible' ? d.hours : 'Always',
  }
}

/** What the browser can tell before the server does; the server's field names, so its answers land in the same places. */
export function recipientErrors(d: RecipientDraft): Record<string, string | undefined> {
  const kind = d.recipientKind
  const errors: Record<string, string | undefined> = {}
  if (kind === 'Address' && !d.recipient.trim()) errors.recipient = recipientHint
  if (kind === 'User' && !d.userId) errors.userId = 'Choose a user.'
  if (kind === 'Group' && !d.groupId) errors.groupId = 'Choose a group.'
  if (kind === 'Responsible' && d.standIn === 'user' && !d.userId) errors.userId = 'Choose the stand-in user.'
  if (kind === 'Responsible' && d.standIn === 'group' && !d.groupId) errors.groupId = 'Choose the stand-in group.'
  if (kind !== 'Address' && !d.byEmail && !d.byPushover) errors.byEmail = 'Choose e-mail, Pushover or both.'
  return errors
}

export function providerIcon(provider: string): string {
  switch (provider) {
    case 'pushover':
      return 'pi pi-mobile'
    case 'http':
      return 'pi pi-link'
    case 'email':
      return 'pi pi-envelope'
    case 'user':
      return 'pi pi-user'
    case 'group':
      return 'pi pi-users'
    case 'responsible':
      return 'pi pi-calendar'
    default:
      return 'pi pi-question-circle'
  }
}

export function providerLabel(provider: string): string {
  switch (provider) {
    case 'http':
      return 'Webhook'
    case 'email':
      return 'E-mail'
    case 'pushover':
      return 'Pushover'
    case 'user':
      return 'User'
    case 'group':
      return 'Group'
    case 'responsible':
      return 'The responsible'
    default:
      return 'Unknown'
  }
}

export function channelsLabel(byEmail: boolean, byPushover: boolean): string {
  return byEmail && byPushover ? 'Pushover and e-mail' : byPushover ? 'Pushover' : byEmail ? 'e-mail' : 'nothing'
}

/** The line under the recipient on the rule's page: the channels, and for the responsible the hours and the stand-in. */
export function recipientDetails(rule: S['NotificationRuleDetail']): string | null {
  const kind = rule.recipientKind ?? 'Address'
  if (kind === 'Address') return null
  const parts: string[] = []
  if (kind === 'Responsible') {
    parts.push(rule.hours === 'WorkingHours' ? 'in working hours only' : 'at any hour')
    const standIn = rule.user?.name ?? rule.group?.name
    parts.push(standIn ? `stand-in ${standIn}` : 'no stand-in')
  }
  parts.push(`by ${channelsLabel(rule.byEmail, rule.byPushover)}`)
  return parts.join(' · ')
}

/** "Anna Muster (responsible): Pushover, e-mail", or for an address the provider it goes to. */
export function reachText(rule: S['NotificationRuleDetail']): string {
  if ((rule.recipientKind ?? 'Address') === 'Address') return `1 message by ${providerLabel(rule.provider)}`
  return rule.reach?.who ?? ''
}
