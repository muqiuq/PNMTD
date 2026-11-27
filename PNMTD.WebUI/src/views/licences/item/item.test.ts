import { describe, expect, it } from 'vitest'
import type { LicenseRequest, LicenseTemplate } from '@/api/licenses'
import {
  addMonths,
  billingText,
  blankRequest,
  headline,
  kindDefaults,
  maskedKey,
  nobodyLine,
  notifyingLines,
  pastCharges,
  seatsFromHostsDefault,
  spentByYear,
  termBar,
  withKind,
  withTemplate,
  type HeadlineItem,
  type Notifying,
  type TermItem,
} from './item'

// Noon local time, so that the browser's day is the one written.
const at = (day: string) => new Date(`${day}T12:00:00`).getTime()
const now = at('2026-11-27')
const today = '2026-11-27'
// The days as the plan writes them, whatever the test machine's locale.
const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const day = (value: string | Date) => {
  const d = typeof value === 'string' ? new Date(`${value}T12:00:00`) : value
  return `${d.getDate()} ${months[d.getMonth()]} ${d.getFullYear()}`
}

describe('the days', () => {
  it('add months as .NET does: at most the month’s last day, counted from the day given', () => {
    expect(addMonths('2026-01-31', 1)).toBe('2026-02-28')
    expect(addMonths('2028-01-31', 1)).toBe('2028-02-29')
    expect(addMonths('2026-01-31', 2)).toBe('2026-03-31')
    expect(addMonths('2027-07-01', -12)).toBe('2026-07-01')
    expect(addMonths('2026-03-31', -1)).toBe('2026-02-28')
    expect(addMonths('2026-12-15', 1)).toBe('2027-01-15')
  })
})

describe('the kinds’ defaults (docs/licences-plan.md §3.3)', () => {
  it('perpetual and one-off: one-time, no expiry, no renewal', () => {
    for (const [type, kind] of [
      ['License', 'Perpetual'],
      ['Service', 'OneOff'],
    ] as const) {
      expect(kindDefaults(type, kind)).toEqual({
        billing: 'OneTime',
        billingMonths: null,
        expiresOn: null,
        termMonths: null,
        renewal: 'None',
        noticeDays: null,
        warnBeforeRenewal: false,
      })
    }
  })

  it('a licence’s subscription is billed yearly for 12 months, renewed by hand; a service’s monthly, renewed automatically', () => {
    expect(kindDefaults('License', 'Subscription')).toMatchObject({
      billing: 'Recurring',
      billingMonths: 12,
      termMonths: 12,
      renewal: 'Manual',
    })
    expect(kindDefaults('Service', 'Subscription')).toMatchObject({
      billing: 'Recurring',
      billingMonths: 1,
      termMonths: 1,
      renewal: 'Automatic',
      warnBeforeRenewal: false,
    })
  })

  it('a contract: monthly, 12 months, automatic, 90 days’ notice, warned before the deadline', () => {
    expect(kindDefaults('Service', 'Contract')).toEqual({
      billing: 'Recurring',
      billingMonths: 1,
      termMonths: 12,
      renewal: 'Automatic',
      noticeDays: 90,
      warnBeforeRenewal: true,
    })
  })

  it('support: yearly, 12 months, by hand, for every type', () => {
    for (const type of ['License', 'Service', 'Hardware'] as const)
      expect(kindDefaults(type, 'Support')).toMatchObject({
        billing: 'Recurring',
        billingMonths: 12,
        termMonths: 12,
        renewal: 'Manual',
      })
  })

  it('usage: a variable price billed monthly, without a term', () => {
    expect(kindDefaults('Service', 'Usage')).toMatchObject({
      billing: 'Variable',
      billingMonths: 1,
      termMonths: null,
      expiresOn: null,
      renewal: 'None',
    })
  })

  it('a purchase and a lease keep their expiry (the warranty’s or the lease’s end) and don’t renew', () => {
    const purchase = kindDefaults('Hardware', 'Purchase')
    expect(purchase).toMatchObject({ billing: 'OneTime', renewal: 'None', termMonths: null })
    expect('expiresOn' in purchase).toBe(false)
    const lease = kindDefaults('Hardware', 'Lease')
    expect(lease).toMatchObject({ billing: 'Recurring', billingMonths: 1, renewal: 'None' })
    expect('expiresOn' in lease).toBe(false)
  })

  it('a trial has no price and keeps its (required) expiry; free has neither price nor expiry; other changes nothing', () => {
    const trial = kindDefaults('License', 'Trial')
    expect(trial).toMatchObject({ priceAmount: null, billing: 'OneTime', renewal: 'None' })
    expect('expiresOn' in trial).toBe(false)
    expect(kindDefaults('Service', 'Free')).toMatchObject({ priceAmount: null, billing: 'OneTime', expiresOn: null })
    expect(kindDefaults('Hardware', 'Other')).toEqual({})
  })

  it('a kind that renews gets the end of its first term as the expiry when there is none', () => {
    const draft: LicenseRequest = { ...blankRequest('Service', today), startsOn: '2026-07-01', expiresOn: null }
    expect(withKind(draft, 'Contract', today)).toMatchObject({ expiresOn: '2027-06-30', renewal: 'Automatic' })
  })

  it('an expiry the defaults filled follows the kind; one entered by hand stays', () => {
    const subscription = blankRequest('Service', today)
    expect(subscription.expiresOn).toBe('2026-12-26')
    expect(withKind(subscription, 'Contract', today).expiresOn).toBe('2027-11-26')
    expect(withKind(subscription, 'Trial', today).expiresOn).toBeNull()
    const set = { ...subscription, expiresOn: '2027-12-31' }
    expect(withKind(set, 'Contract', today).expiresOn).toBe('2027-12-31')
    expect(withKind(set, 'Trial', today).expiresOn).toBe('2027-12-31')
    expect(withKind(set, 'Perpetual', today).expiresOn).toBeNull()
  })

  it('a new item starts today with its type’s first kind; a licence in devices counts its seats on its hosts', () => {
    expect(blankRequest('License', today)).toMatchObject({
      kind: 'Perpetual',
      billing: 'OneTime',
      startsOn: today,
      seatsFromHosts: true,
      reviewMonths: 12,
    })
    expect(blankRequest('Service', today)).toMatchObject({
      kind: 'Subscription',
      renewal: 'Automatic',
      expiresOn: '2026-12-26',
      seatsFromHosts: false,
    })
    expect(seatsFromHostsDefault('License', 'Cores')).toBe(true)
    expect(seatsFromHostsDefault('License', 'users')).toBe(false)
    expect(seatsFromHostsDefault('Hardware', 'devices')).toBe(false)
  })

  it('a template brings its product, its kind’s defaults and then its own, and names the item', () => {
    const template = {
      id: 't1',
      name: 'M365 BS',
      type: 'License',
      categoryId: 'c1',
      categoryName: 'Cloud & SaaS',
      manufacturer: 'Microsoft',
      productName: 'Microsoft 365',
      edition: 'Business Standard',
      vendor: 'Contoso',
      productUrl: null,
      kind: 'Subscription',
      seats: 25,
      seatUnit: 'users',
      seatsFromHosts: null,
      priceAmount: 12.5,
      currency: 'EUR',
      priceBasis: 'PerSeat',
      billing: null,
      billingMonths: 1,
      termMonths: null,
      renewal: null,
      warnDays: null,
      noticeDays: null,
      warnBeforeRenewal: null,
      reviewMonths: null,
      supportContact: null,
      supportUrl: null,
      sla: null,
      endOfLifeOn: null,
      endOfLifeNote: null,
      note: null,
      items: 0,
      followEndOfLife: 0,
      createdAt: '',
      createdBy: null,
      updatedAt: '',
    } satisfies LicenseTemplate
    const draft = withTemplate(blankRequest('Service', today), template, today)
    expect(draft).toMatchObject({
      type: 'License',
      templateId: 't1',
      categoryId: 'c1',
      name: 'Microsoft 365 Business Standard',
      kind: 'Subscription',
      billing: 'Recurring',
      billingMonths: 1,
      termMonths: 12,
      renewal: 'Manual',
      expiresOn: '2027-11-26',
      seats: 25,
      seatUnit: 'users',
      seatsFromHosts: false,
      priceAmount: 12.5,
      priceBasis: 'PerSeat',
      endOfLifeFromTemplate: true,
    })
  })
})

describe('the headline (§8.7)', () => {
  const item = (patch: Partial<HeadlineItem>): HeadlineItem => ({
    status: 'Active',
    statusChangedAt: null,
    expiresOn: '2027-01-01',
    kind: 'Subscription',
    renewal: 'Manual',
    warnDays: 30,
    cancelBy: null,
    ...patch,
  })

  it('counts the days left, and says when it expires within the warning', () => {
    expect(headline(item({}), now, day)).toBe('Valid · 35 days left')
    expect(headline(item({ expiresOn: '2026-12-02' }), now, day)).toBe('Expires in 5 days')
    expect(headline(item({ expiresOn: '2026-11-28' }), now, day)).toBe('Expires tomorrow')
    expect(headline(item({ expiresOn: today }), now, day)).toBe('Expires today')
    expect(headline(item({ expiresOn: '2026-11-25' }), now, day)).toBe('Expired 2 days ago')
    expect(headline(item({ expiresOn: '2026-11-26' }), now, day)).toBe('Expired yesterday')
    expect(headline(item({ kind: 'Trial', expiresOn: '2026-12-02' }), now, day)).toBe('Trial ends in 5 days')
  })

  it("says Doesn't expire without an expiry", () => {
    expect(headline(item({ expiresOn: null, kind: 'Perpetual', renewal: 'None' }), now, day)).toBe("Doesn't expire")
  })

  it('an automatic renewal names its cancellation deadline', () => {
    expect(headline(item({ renewal: 'Automatic', expiresOn: '2027-03-02', cancelBy: '2027-01-01' }), now, day)).toBe(
      'Renews automatically in 95 days · cancel by 1 Jan 2027',
    )
    expect(headline(item({ renewal: 'Automatic', expiresOn: '2027-03-02', cancelBy: null }), now, day)).toBe(
      'Renews automatically in 95 days',
    )
    expect(headline(item({ renewal: 'Automatic', expiresOn: '2026-12-02', cancelBy: '2026-11-01' }), now, day)).toBe(
      'Renews automatically in 5 days · the deadline to cancel was 1 Nov 2026',
    )
  })

  it('hardware has a warranty or a lease', () => {
    expect(headline(item({ kind: 'Purchase', renewal: 'None', expiresOn: '2028-04-30' }), now, day)).toBe(
      'Warranty until 30 Apr 2028',
    )
    expect(headline(item({ kind: 'Purchase', renewal: 'None', expiresOn: '2026-12-02' }), now, day)).toBe(
      'Warranty ends in 5 days',
    )
    expect(headline(item({ kind: 'Purchase', renewal: 'None', expiresOn: '2026-11-20' }), now, day)).toBe(
      'Warranty ended 7 days ago',
    )
    expect(headline(item({ kind: 'Lease', renewal: 'None', expiresOn: '2027-06-30' }), now, day)).toBe(
      'Lease ends on 30 Jun 2027',
    )
  })

  it('a cancelled item ends; a retired one says since when', () => {
    expect(headline(item({ status: 'Cancelled', renewal: 'Automatic' }), now, day)).toBe('Ends in 35 days (cancelled)')
    expect(headline(item({ status: 'Cancelled', expiresOn: '2026-11-20' }), now, day)).toBe(
      'Ended 7 days ago (cancelled)',
    )
    expect(headline(item({ status: 'Retired', statusChangedAt: '2026-11-03T10:00:00Z' }), now, day)).toBe(
      'Retired on 3 Nov 2026',
    )
  })
})

describe('the term’s bar (§8.7)', () => {
  const item = (patch: Partial<TermItem>): TermItem => ({
    startsOn: '2025-03-15',
    expiresOn: '2026-12-31',
    termMonths: 12,
    warnDays: 30,
    renewal: 'Manual',
    status: 'Active',
    cancelBy: null,
    warnBeforeRenewal: false,
    endOfLifeOn: null,
    ...patch,
  })

  it('runs from the current term’s first day to the expiry, marking today and the warning’s start', () => {
    const bar = termBar(item({}), at('2026-07-02'))!
    expect(bar.start).toBe('2026-01-01')
    expect(bar.end).toBe('2026-12-31')
    // 182 of 365 days gone; the warning from 1 Dec (day 334).
    expect(bar.today).toBeCloseTo((182 / 365) * 100, 5)
    expect(bar.warnFrom).toBe('2026-12-01')
    expect(bar.warn).toBeCloseTo((334 / 365) * 100, 5)
    expect(bar.expired).toBe(false)
    expect(bar.endOfLife).toBeNull()
  })

  it('starts on the start without a term, never before it, and keeps today within the bar', () => {
    expect(termBar(item({ termMonths: null }), now)!.start).toBe('2025-03-15')
    expect(termBar(item({ startsOn: '2026-06-01' }), now)!.start).toBe('2026-06-01')
    const past = termBar(item({}), at('2027-01-05'))!
    expect(past.today).toBe(100)
    expect(past.expired).toBe(true)
  })

  it('marks the end of life only within the term, and no warning with 0 days', () => {
    expect(termBar(item({ endOfLifeOn: '2026-10-01' }), now)!.endOfLife).toBeCloseTo((273 / 365) * 100, 5)
    expect(termBar(item({ endOfLifeOn: '2027-10-01' }), now)!.endOfLife).toBeNull()
    expect(termBar(item({ warnDays: 0 }), now)!.warn).toBeNull()
  })

  it('an automatic renewal warns before its cancellation deadline, if asked, and marks the deadline', () => {
    const quiet = termBar(item({ renewal: 'Automatic', cancelBy: '2026-10-02' }), now)!
    expect(quiet.warn).toBeNull()
    expect(quiet.cancelBy).toBeCloseTo((274 / 365) * 100, 5)
    const warned = termBar(item({ renewal: 'Automatic', cancelBy: '2026-10-02', warnBeforeRenewal: true }), now)!
    expect(warned.warnFrom).toBe('2026-09-02')
  })

  it('is not drawn without an expiry or a start', () => {
    expect(termBar(item({ expiresOn: null }), now)).toBeNull()
    expect(termBar(item({ startsOn: null, termMonths: null }), now)).toBeNull()
  })
})

describe('who hears about it (§5.2)', () => {
  const n = (patch: Partial<Notifying>): Notifying => ({
    hasOwner: false,
    ownerHasEmail: false,
    hosts: 0,
    unownedChoosers: 0,
    nobody: false,
    ...patch,
  })

  it('names the owner, the hosts’ sensors and those who chose the items nobody owns', () => {
    expect(notifyingLines(n({ hasOwner: true, ownerHasEmail: true, hosts: 3 }))).toEqual([
      'the owner, by e-mail',
      "the 3 hosts' sensors, through their rules",
    ])
    expect(notifyingLines(n({ hosts: 1 }))).toEqual(["the host's sensor, through its rules"])
    expect(notifyingLines(n({ unownedChoosers: 2 }))).toEqual(['2 people who chose items nobody owns'])
    expect(notifyingLines(n({ unownedChoosers: 1 }))).toEqual(['1 person who chose items nobody owns'])
  })

  it('says nobody, and that the owner has no e-mail address', () => {
    expect(notifyingLines(n({ nobody: true }))).toEqual(['nobody'])
    expect(notifyingLines(n({ hasOwner: true, nobody: true }))).toEqual([
      'the owner has no e-mail address in PNMT',
      'nobody',
    ])
  })

  it('warns on the page only when nobody is told', () => {
    expect(nobodyLine(n({ nobody: true }))).toBe('On no host and without an owner, so nobody is told when it expires.')
    expect(nobodyLine(n({ nobody: true, hasOwner: true }))).toBe(
      'On no host, and its owner has no e-mail address in PNMT, so nobody is told when it expires.',
    )
    expect(nobodyLine(n({ hasOwner: true, ownerHasEmail: true }))).toBeNull()
  })
})

describe('the charges since the start (§6.2)', () => {
  const draft = {
    billing: 'Recurring',
    billingMonths: 1,
    termMonths: null,
    startsOn: '2023-07-01',
    expiresOn: null,
    priceAmount: 990,
    priceBasis: 'Total',
    seats: null,
    recordCharges: true,
  } as const

  it('counts the billing days from the start to before today', () => {
    // Jul 2023 … Nov 2026: 6 + 12 + 12 + 11.
    expect(pastCharges(draft, today)).toBe(41)
    expect(pastCharges({ ...draft, startsOn: '2026-11-01' }, today)).toBe(1)
    expect(pastCharges({ ...draft, billingMonths: 12 }, today)).toBe(4)
  })

  it('steps from the current term’s first day, and stops at the expiry', () => {
    // A 12-month term ending 31 Mar 2027, billed every 3 months: 1 Apr, 1 Jul, 1 Oct 2026 from a start on 15 Feb 2026.
    expect(
      pastCharges(
        { ...draft, billingMonths: 3, termMonths: 12, startsOn: '2026-02-15', expiresOn: '2027-03-31' },
        today,
      ),
    ).toBe(3)
    expect(pastCharges({ ...draft, startsOn: '2026-01-01', expiresOn: '2026-06-30' }, today)).toBe(6)
  })

  it('offers nothing for a one-time price, without a price, from today on, or while PNMT records none', () => {
    expect(pastCharges({ ...draft, billing: 'OneTime' }, today)).toBe(0)
    expect(pastCharges({ ...draft, priceAmount: null }, today)).toBe(0)
    expect(pastCharges({ ...draft, priceBasis: 'PerSeat' }, today)).toBe(0)
    expect(pastCharges({ ...draft, startsOn: today }, today)).toBe(0)
    expect(pastCharges({ ...draft, recordCharges: false }, today)).toBe(0)
    expect(pastCharges({ ...draft, billing: 'Variable' }, today)).toBe(41)
  })
})

describe('the words', () => {
  it('spending per year, newest first, per currency', () => {
    expect(
      spentByYear([
        { on: '2025-03-01', amount: 100, currency: 'EUR' },
        { on: '2026-01-01', amount: 330, currency: 'EUR' },
        { on: '2026-02-01', amount: 330.1, currency: 'EUR' },
        { on: '2026-02-01', amount: 50, currency: 'CHF' },
      ]),
    ).toEqual([
      {
        year: 2026,
        amounts: [
          { currency: 'CHF', amount: 50 },
          { currency: 'EUR', amount: 660.1 },
        ],
      },
      { year: 2025, amounts: [{ currency: 'EUR', amount: 100 }] },
    ])
  })

  it('billing, and a key before it is shown', () => {
    expect(billingText('OneTime', null)).toBe('one-time')
    expect(billingText('Recurring', 1)).toBe('every month')
    expect(billingText('Recurring', 12)).toBe('every 12 months')
    expect(billingText('Variable', 1)).toBe('variable, every month')
    expect(maskedKey('CC9DR')).toBe('•••••-CC9DR')
    expect(maskedKey(null)).toBe('•••••')
  })
})
