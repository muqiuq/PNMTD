import { describe, expect, it } from 'vitest'
import type { S } from '@/api/resources'
import {
  channelsLabel,
  providerIcon,
  providerLabel,
  reachText,
  recipientDetails,
  recipientDraft,
  recipientErrors,
  recipientRequest,
} from './ruleHelp'

const detail = (patch: Partial<S['NotificationRuleDetail']>): S['NotificationRuleDetail'] => ({
  id: 'r1',
  name: 'Storage on duty',
  recipient: 'The responsible of the day',
  provider: 'responsible',
  type: 'ALWAYS',
  enabled: true,
  sensors: [],
  recipientKind: 'Responsible',
  byEmail: true,
  byPushover: true,
  hours: 'Always',
  ...patch,
})

describe('the recipient kinds', () => {
  it('have an icon and a label each, the three new ones too', () => {
    expect(['email', 'pushover', 'http', 'user', 'group', 'responsible'].map(providerIcon)).toEqual([
      'pi pi-envelope',
      'pi pi-mobile',
      'pi pi-link',
      'pi pi-user',
      'pi pi-users',
      'pi pi-calendar',
    ])
    expect(['user', 'group', 'responsible', 'other'].map(providerLabel)).toEqual([
      'User',
      'Group',
      'The responsible',
      'Unknown',
    ])
  })

  it('reads a rule into a draft, the stand-in from whichever is set', () => {
    expect(recipientDraft().recipientKind).toBe('Address')
    const standIn = recipientDraft(detail({ group: { id: 'g1', name: 'Storage' } }))
    expect(standIn).toMatchObject({ recipientKind: 'Responsible', groupId: 'g1', userId: null, standIn: 'group' })
    expect(recipientDraft(detail({ hours: 'WorkingHours' })).standIn).toBe('nobody')
    const address = recipientDraft(
      detail({ recipientKind: 'Address', recipient: 'ops@example.com', provider: 'email' }),
    )
    expect(address).toMatchObject({
      recipientKind: 'Address',
      recipient: 'ops@example.com',
      byEmail: true,
      byPushover: true,
    })
  })

  it('sends only what the kind needs', () => {
    const draft = recipientDraft(detail({ user: { id: 'u1', name: 'Anna' }, hours: 'WorkingHours', byPushover: false }))
    expect(recipientRequest(draft)).toEqual({
      recipientKind: 'Responsible',
      recipient: null,
      userId: 'u1',
      groupId: null,
      byEmail: true,
      byPushover: false,
      hours: 'WorkingHours',
    })
    // Switching to a user keeps the id, drops the hours; switching to an address drops the channels.
    expect(recipientRequest({ ...draft, recipientKind: 'User' })).toMatchObject({
      userId: 'u1',
      hours: 'Always',
      groupId: null,
    })
    expect(recipientRequest({ ...draft, recipientKind: 'Address', recipient: ' x@example.com ' })).toEqual({
      recipientKind: 'Address',
      recipient: 'x@example.com',
      userId: null,
      groupId: null,
      byEmail: true,
      byPushover: true,
      hours: 'Always',
    })
    // The stand-in choice decides which id goes.
    expect(recipientRequest({ ...draft, standIn: 'nobody' })).toMatchObject({ userId: null, groupId: null })
  })

  it("tells what is missing before the server does, by the server's field names", () => {
    expect(recipientErrors(recipientDraft())).toHaveProperty('recipient')
    expect(recipientErrors({ ...recipientDraft(), recipientKind: 'User' })).toHaveProperty('userId')
    expect(
      recipientErrors({ ...recipientDraft(), recipientKind: 'Group', byEmail: false, byPushover: false }),
    ).toMatchObject({
      groupId: 'Choose a group.',
      byEmail: 'Choose e-mail, Pushover or both.',
    })
    expect(recipientErrors({ ...recipientDraft(), recipientKind: 'Responsible', standIn: 'user' })).toHaveProperty(
      'userId',
    )
    expect(recipientErrors({ ...recipientDraft(), recipientKind: 'Responsible' })).toEqual({})
  })

  it('describes the rule in one line', () => {
    expect(channelsLabel(true, false)).toBe('e-mail')
    expect(recipientDetails(detail({ group: { id: 'g1', name: 'Storage' }, hours: 'WorkingHours' }))).toBe(
      'in working hours only · stand-in Storage · by Pushover and e-mail',
    )
    expect(recipientDetails(detail({ recipientKind: 'Group', byPushover: false }))).toBe('by e-mail')
    expect(recipientDetails(detail({ recipientKind: 'Address', provider: 'email' }))).toBeNull()
    expect(reachText(detail({ recipientKind: 'Address', provider: 'http' }))).toBe('1 message by Webhook')
    expect(
      reachText(
        detail({ reach: { people: 1, deliveries: 2, who: 'Anna (responsible): Pushover, e-mail', why: null } }),
      ),
    ).toBe('Anna (responsible): Pushover, e-mail')
  })
})
