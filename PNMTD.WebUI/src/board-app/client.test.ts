import { describe, expect, it } from 'vitest'
import { BoardError, tokenFromPath } from './client'

/** docs/boards-plan.md §5.1, §6.4: the token from the page's address, and which answers fill the screen. */
describe('board app client', () => {
  const token = 'pnmb_' + 'A'.repeat(43)

  it('reads the token from /pnmt-board/<token>', () => {
    expect(tokenFromPath(`/pnmt-board/${token}`)).toBe(token)
    expect(tokenFromPath(`/pnmt-board/${token}/`)).toBe(token)
    expect(tokenFromPath('/pnmt-board/')).toBeNull()
    expect(tokenFromPath('/pnmt-board/pnmb_short')).toBeNull()
  })

  it('shows a refusal full screen, and retries a passing failure', () => {
    expect(new BoardError(403, 'board_network', '').refused).toBe(true)
    expect(new BoardError(404, 'board_not_found', '').refused).toBe(true)
    expect(new BoardError(0, 'network', '').refused).toBe(false)
    expect(new BoardError(500, 'error', '').refused).toBe(false)
  })
})
