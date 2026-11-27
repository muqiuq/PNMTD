import { describe, expect, it } from 'vitest'
import type { LicenseTemplate } from '@/api/licenses'
import {
  endOfLifeChanged,
  matchesTemplate,
  mergeTarget,
  moved,
  priceText,
  sortTemplates,
  templateDraft,
  templateRequest,
  termText,
  usesText,
} from './templates'

function template(name: string, more: Partial<LicenseTemplate> = {}): LicenseTemplate {
  return {
    id: name,
    name,
    type: 'License',
    categoryId: 'cat',
    categoryName: 'Software',
    manufacturer: null,
    productName: name,
    edition: null,
    vendor: null,
    productUrl: null,
    kind: null,
    seats: null,
    seatUnit: null,
    seatsFromHosts: null,
    priceAmount: null,
    currency: null,
    priceBasis: null,
    billing: null,
    billingMonths: null,
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
    createdAt: '2026-11-20T10:00:00Z',
    createdBy: null,
    updatedAt: '2026-11-20T10:00:00Z',
    ...more,
  }
}

const names = (list: LicenseTemplate[]) => list.map((t) => t.name)

describe('the templates (docs/licences-plan.md §8.6)', () => {
  it("sorts by a column, nothing set last both ways, ties by name; the server's order without a sort", () => {
    const list = [
      template('Windows Server 2016', { endOfLifeOn: '2027-01-12', items: 3 }),
      template('Veeam B&R', { items: 7 }),
      template('Acrobat', { endOfLifeOn: '2026-12-31', items: 3 }),
    ]
    expect(names(sortTemplates(list, null))).toEqual(['Windows Server 2016', 'Veeam B&R', 'Acrobat'])
    expect(names(sortTemplates(list, { field: 'eol', order: 1 }))).toEqual([
      'Acrobat',
      'Windows Server 2016',
      'Veeam B&R',
    ])
    expect(names(sortTemplates(list, { field: 'eol', order: -1 }))).toEqual([
      'Windows Server 2016',
      'Acrobat',
      'Veeam B&R',
    ])
    expect(names(sortTemplates(list, { field: 'items', order: -1 }))).toEqual([
      'Veeam B&R',
      'Acrobat',
      'Windows Server 2016',
    ])
  })

  it('searches every word in the name, type, category and product', () => {
    const t = template('M365 Business Standard', { manufacturer: 'Microsoft', vendor: 'Contoso', type: 'Service' })
    expect(matchesTemplate(t, 'microsoft contoso')).toBe(true)
    expect(matchesTemplate(t, 'service software')).toBe(true)
    expect(matchesTemplate(t, 'hetzner')).toBe(false)
    expect(matchesTemplate(t, '')).toBe(true)
  })

  it('words the default term and price', () => {
    expect(termText(template('a'))).toBe('–')
    expect(termText(template('a', { termMonths: 12, renewal: 'Automatic' }))).toBe('12 months · renews automatically')
    expect(priceText(template('a'))).toBe('–')
    expect(
      priceText(template('a', { priceAmount: 330, currency: 'EUR', billing: 'Recurring', billingMonths: 1 })),
    ).toBe('EUR 330.00 a month')
    expect(
      priceText(
        template('a', {
          priceAmount: 12.5,
          currency: 'EUR',
          priceBasis: 'PerSeat',
          billing: 'Recurring',
          billingMonths: 12,
        }),
      ),
    ).toBe('EUR 12.50 per seat a year')
    expect(
      priceText(template('a', { priceAmount: 1100, currency: 'EUR', billing: 'Variable', billingMonths: 1 })),
    ).toBe('~EUR 1,100.00 a month')
    expect(priceText(template('a', { priceAmount: 890, billing: 'OneTime' }))).toBe('890.00 one-time')
    expect(priceText(template('a', { billing: 'Recurring', billingMonths: 3 }))).toBe('every 3 months')
  })

  it('makes a draft of a copy and a request of the draft: text trimmed, empty as null', () => {
    const t = template('Windows Server 2016', { manufacturer: 'Microsoft', currency: 'EUR', note: 'n' })
    const draft = templateDraft(t)
    draft.name = '  Windows Server 2016 Std  '
    draft.edition = '  '
    draft.currency = 'chf'
    draft.billing = 'OneTime'
    draft.billingMonths = 12
    expect(t.name).toBe('Windows Server 2016')
    expect(templateRequest(draft)).toMatchObject({
      name: 'Windows Server 2016 Std',
      edition: null,
      currency: 'CHF',
      manufacturer: 'Microsoft',
      billingMonths: null,
      note: 'n',
    })
    expect(templateDraft(null, 'Service')).toMatchObject({ type: 'Service', name: '', kind: null, categoryId: null })
  })

  it('tells when the end of life changes, for its followers', () => {
    const t = template('a', { endOfLifeOn: '2027-01-12', endOfLifeNote: 'end of extended support' })
    const draft = templateDraft(t)
    expect(endOfLifeChanged(t, draft)).toBe(false)
    draft.endOfLifeNote = ' end of extended support '
    expect(endOfLifeChanged(t, draft)).toBe(false)
    draft.endOfLifeOn = '2027-10-14'
    expect(endOfLifeChanged(t, draft)).toBe(true)
    expect(endOfLifeChanged(null, draft)).toBe(false)
  })
})

describe('the categories and the names (§8.6)', () => {
  it('moves a category up or down, and nowhere past either end', () => {
    expect(moved(['a', 'b', 'c'], 'b', -1)).toEqual(['b', 'a', 'c'])
    expect(moved(['a', 'b', 'c'], 'b', 1)).toEqual(['a', 'c', 'b'])
    expect(moved(['a', 'b', 'c'], 'a', -1)).toEqual(['a', 'b', 'c'])
    expect(moved(['a', 'b', 'c'], 'c', 1)).toEqual(['a', 'b', 'c'])
  })

  it('finds the name a rename merges into', () => {
    const list = [
      { name: 'Microsoft', items: 14, templates: 2 },
      { name: 'Microsoft Corp.', items: 3, templates: 0 },
    ]
    expect(mergeTarget(list, 'Microsoft Corp.', ' Microsoft ')?.items).toBe(14)
    expect(mergeTarget(list, 'Microsoft Corp.', 'Microsoft Corporation')).toBeNull()
    expect(mergeTarget(list, 'Microsoft Corp.', 'Microsoft Corp.')).toBeNull()
    expect(usesText(14, 2)).toBe('14 items and 2 templates')
    expect(usesText(1, 0)).toBe('1 item')
    expect(usesText(0, 0)).toBe('nothing')
  })
})
