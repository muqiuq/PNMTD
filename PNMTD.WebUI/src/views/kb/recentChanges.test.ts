import { describe, expect, it } from 'vitest'
import { changeLine, moreLine } from './recentChanges'

describe('the recent changes', () => {
  it('say what happened and who did it', () => {
    expect(changeLine({ kind: 'created', detail: null, byName: 'Ben' })).toBe('created by Ben')
    expect(changeLine({ kind: 'edited', detail: null, byName: 'Anna' })).toBe('edited by Anna')
    expect(changeLine({ kind: 'edited', detail: 'Draft → Published', byName: 'Anna' })).toBe(
      'edited by Anna, status Draft → Published',
    )
    expect(changeLine({ kind: 'status', detail: 'Published → Needs review', byName: 'Eve' })).toBe(
      'status Published → Needs review by Eve',
    )
    expect(changeLine({ kind: 'restored', detail: 'from 4', byName: 'Anna' })).toBe('restored from 4 by Anna')
  })

  it('count the other changes only when there were some', () => {
    expect(moreLine({ changes: 1, people: 1 })).toBeNull()
    expect(moreLine({ changes: 4, people: 2 })).toBe('4 changes by 2 people')
    expect(moreLine({ changes: 2, people: 1 })).toBe('2 changes by 1 person')
  })
})
