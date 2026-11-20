import { expect, test, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * The lounge (docs/lounge-plan.md §15, §16 Web) on a stack with Cookie__Enabled=true: the room for every signed-in user,
 * with the dev seed's conversation; started on Admin → Cookie → Lounge with a group to read with; a message sent; a comment
 * on a message, which stands under it. The voices' talk itself needs a model (PNMTD.Tests' LoungeTests). With this
 * stack's unreachable guard, both are stored marked as not read (the post fails closed). Skipped without Cookie.
 */
async function api(page: Page, method: 'GET' | 'PUT' | 'POST', path: string, data?: unknown) {
  const token = (await page.context().cookies()).find((c) => c.name === 'XSRF-TOKEN')?.value ?? ''
  const response = await page.request.fetch(path, { method, data, headers: { 'X-XSRF-TOKEN': token } })
  expect(response.ok(), `${method} ${path}: ${response.status()}`).toBeTruthy()
  return response.status() === 204 ? null : response.json()
}

test('the room, the lounge started, a message and a comment on a message', async ({ page }) => {
  test.setTimeout(120_000)
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  const status = await api(page, 'GET', '/api/v1/cookie/status')
  test.skip(!status.cookie.enabled, 'Cookie__Enabled is not set for this stack')

  // The room: in the menu for everyone, readable while stopped.
  // The drawer's link is named "Lounge Lounge": its label and a copy for screen readers.
  await page
    .getByRole('navigation')
    .getByRole('link', { name: /^Lounge\b/ })
    .first()
    .click()
  await expect(page.getByRole('heading', { level: 1, name: 'Lounge' })).toBeVisible()
  const room = page.getByRole('region', { name: 'The conversation' })
  await expect(room).toBeVisible()
  const seeded = (await api(page, 'GET', '/api/v1/lounge')).messages as { text: string; from: string }[]
  const said = seeded.filter((m) => m.from === 'cookie' || m.from === 'seraph')
  if (said.length) await expect(room).toContainText(said[said.length - 1]!.text.slice(0, 40))
  await expect(page.getByRole('complementary', { name: 'Never forgotten, and the convictions' })).toBeVisible()
  await shot(page, 'lounge')

  // Admin → Cookie → Lounge: a group to read with, then Start (its own, independent of Cookie's).
  const settings = await api(page, 'GET', '/api/v1/cookie/admin/settings')
  const people = await api(page, 'GET', '/api/v1/cookie/admin/people')
  const group = people.groups[0]?.id as string | undefined
  test.skip(!group, 'no group on this stack')
  await api(page, 'PUT', '/api/v1/cookie/admin/settings/lounge', { ...settings.lounge, groupId: group })
  await page.goto('/pnmt-admin/admin/cookie/lounge')
  await expect(page.getByRole('region', { name: 'Reads with' })).toBeVisible()
  await shot(page, 'cookie-admin-lounge')
  const start = page.getByRole('button', { name: 'Start', exact: true })
  if (await start.isVisible()) {
    await expect(start).toBeEnabled()
    await start.click()
  }
  await expect(page.getByRole('button', { name: 'Stop', exact: true })).toBeVisible()

  try {
    // A message: the composer is there with Lounge: Use (an admin has it).
    await page.goto('/pnmt-admin/lounge')
    const composer = page.getByRole('textbox', { name: 'Your message' })
    await expect(composer).toBeVisible()
    await composer.fill('@Seraph how is the NAS?')
    const posted = page.waitForResponse(
      (r) => r.url().endsWith('/api/v1/lounge/messages') && r.request().method() === 'POST',
    )
    await page.getByRole('button', { name: 'Send', exact: true }).click()
    const response = await posted
    expect(response.ok(), `POST /api/v1/lounge/messages: ${response.status()}`).toBeTruthy()
    const sent = (await response.json()) as { id: number; refused: boolean }
    // Started, the room never reads as closed, even before the loop's first step.
    await expect(page.getByText('The lounge is closed. The conversation stays readable.')).toHaveCount(0)
    const line = room.locator(`[data-id="${sent.id}"]`)
    await expect(line).toContainText('@Seraph how is the NAS?')
    // Jev unreachable (this stack's guard): the line is stored and marked, never read by a model (fails closed).
    if (sent.refused) await expect(line).toContainText("Cookie and Seraph didn't read this one.")

    // A comment on that message: under it, marked the same way, or not yet read where Jev answers.
    await line.getByRole('button', { name: /^Comment on .+'s message$/ }).click()
    // The composer names the message the comment is on, beside the button that takes it back to the conversation.
    const onTarget = page.getByRole('button', { name: 'Comment on the conversation instead' })
    await expect(onTarget).toBeVisible()
    await expect(onTarget.locator('..')).toContainText("'s message of")
    await page.getByRole('textbox', { name: 'Your comment' }).fill('I cleaned it up yesterday.')
    const commented = page.waitForResponse(
      (r) => r.url().endsWith('/api/v1/lounge/messages') && r.request().method() === 'POST',
    )
    await page.getByRole('button', { name: 'Send', exact: true }).click()
    const comment = (await (await commented).json()) as { id: number; refused: boolean }
    const under = line.locator(`.comments [data-id="${comment.id}"]`)
    await expect(under).toContainText('I cleaned it up yesterday.')
    await expect(under).toContainText(comment.refused ? "Cookie and Seraph didn't read this one." : 'not yet read')
    await shot(page, 'lounge-comment')
  } finally {
    await api(page, 'POST', '/api/v1/cookie/admin/lounge/stop')
  }
  expect(problems).toEqual([])
})
