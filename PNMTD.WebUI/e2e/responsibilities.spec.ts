import { expect, test, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * Responsibilities' extensions (docs/responsibilities-extensions-plan.md §11) on the seeded stack's three responsibilities: Network
 * (anna, ben), Backup (ben) and Printers (the global responsible stands in). Each test leaves them as it found them.
 */

const list = '/pnmt-admin/admin/responsibilities'

async function openList(page: Page) {
  await page.goto(list)
  await expect(
    page.locator('[data-test="responsibilities-table"]').getByRole('link', { name: 'Network' }),
  ).toBeVisible()
}

const names = async (page: Page) =>
  page.locator('[data-test="responsibilities-table"]').locator('tbody tr td:first-child a').allInnerTexts()

test('the list sorts by its columns, keeps the sort in the URL, and a colour shows where the responsibility is named', async ({
  page,
}) => {
  const problems = collectProblems(page)
  await signIn(page)
  await openList(page)
  expect(await names(page)).toEqual(['Backup', 'Network', 'Printers'])

  // By the responsible: anna's Network, ben's Backup; Printers sorts by the global responsible standing in.
  await page.getByRole('columnheader', { name: 'Responsible' }).click()
  await expect(page).toHaveURL(/sort=responsible/)
  const ascending = await names(page)
  await page.getByRole('columnheader', { name: 'Responsible' }).click()
  await expect(page).toHaveURL(/sort=-responsible/)
  await expect.poll(() => names(page)).toEqual([...ascending].reverse())
  await shot(page, 'responsibilities-sorted')

  // A responsibility's page and back: the same sort.
  await page.locator('[data-test="responsibilities-table"]').getByRole('link', { name: 'Network' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Network' })).toBeVisible()
  await page.goBack()
  await expect(page).toHaveURL(/sort=-responsible/)
  await expect.poll(() => names(page)).toEqual([...ascending].reverse())

  // A colour: picked on the details form, shown as a dot on the list and on a linked host's panel.
  await page.locator('[data-test="responsibilities-table"]').getByRole('link', { name: 'Network' }).click()
  const details = page.getByRole('region', { name: 'Details' })
  await details.getByRole('button', { name: 'Red' }).click()
  await details.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByText('Network saved')).toBeVisible()
  await page.getByRole('link', { name: 'fw-01' }).click()
  const panel = page.locator('[data-test="responsibilities-panel"]')
  await expect(panel.locator('[data-test="colour-dot"]')).toHaveAttribute('title', 'Red')
  await shot(page, 'responsibilities-host-panel-colour')

  // Back as it was: the seed's blue.
  await page.goBack()
  await details.getByRole('button', { name: 'Blue' }).click()
  await details.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByText('Network saved')).toBeVisible()
  expect(problems).toEqual([])
})

test('the Links tab filters every link, and links a host nobody owns', async ({ page }) => {
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto(`${list}/links`)
  const rows = page.locator('.list-table tbody tr')
  await expect(rows.first()).toBeVisible()

  // The search: an object's name.
  await page.getByPlaceholder('Object, where, responsibility…').fill('fw-0')
  await expect(page).toHaveURL(/q=fw-0/)
  await expect(rows).toHaveCount(2)
  await expect(rows.first()).toContainText('Network')
  await shot(page, 'responsibilities-links')

  // What nobody links: dns-01, linked to Backup from here.
  await page.locator('[data-test="links-show"]').click()
  await page.getByRole('option', { name: 'Hosts without a responsibility' }).click()
  await expect(page).toHaveURL(/show=hosts/)
  await page.getByPlaceholder('Name, where…').fill('dns-01')
  await expect(rows).toHaveCount(1)
  await rows.first().getByRole('checkbox').check()
  await page.locator('[data-test="link-selected"]').click()
  const dialog = page.getByRole('dialog', { name: 'Link 1 host to…' })
  await dialog.locator('[data-test="bulk-target"]').click()
  await page.getByRole('option', { name: 'Backup' }).click()
  await dialog.locator('[data-test="bulk-submit"]').click()
  await expect(page.getByText('Linked 1 object to Backup')).toBeVisible()
  await expect(page.getByText('Every one of these hosts has a responsibility.')).toBeVisible()

  // On the links again, and unlinked, as the seed had it.
  await page.locator('[data-test="links-show"]').click()
  await page.getByRole('option', { name: 'Links', exact: true }).click()
  await page.getByPlaceholder('Object, where, responsibility…').fill('dns-01')
  await expect(rows).toHaveCount(1)
  await expect(rows.first()).toContainText('Backup')
  await rows.first().getByRole('checkbox').check()
  await page.locator('[data-test="links-selected"]').getByRole('button', { name: 'Unlink' }).click()
  await page
    .getByRole('alertdialog', { name: 'Unlink 1 link from Backup?' })
    .getByRole('button', { name: 'Unlink' })
    .click()
  await expect(page.getByText('Unlinked 1 link')).toBeVisible()
  expect(problems).toEqual([])
})

test('the overview: responsibilities moved by hand, saved for everyone, and back to automatic', async ({ page }) => {
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto(`${list}/overview?layers=people,hosts,notes`)
  const map = page.getByTestId('responsibilities-map')
  const node = map.getByRole('button', { name: /^Responsibility Network/ })
  await expect(node).toBeVisible()
  await expect(map.getByRole('button', { name: /^Person / }).first()).toBeVisible()
  await expect(map.getByRole('button', { name: /^Host fw-01/ })).toBeVisible()
  await shot(page, 'responsibilities-overview')

  const centre = async () => {
    const b = (await node.locator('circle.body').boundingBox())!
    return { x: Math.round(b.x + b.width / 2), y: Math.round(b.y + b.height / 2) }
  }
  const onMap = async () => {
    const c = await centre()
    const m = (await map.boundingBox())!
    return { x: c.x - Math.round(m.x), y: c.y - Math.round(m.y) }
  }
  const near = (a: { x: number; y: number }, b: { x: number; y: number }) =>
    Math.abs(a.x - b.x) <= 1 && Math.abs(a.y - b.y) <= 1
  const drag = async (dx: number, dy: number) => {
    const from = await centre()
    await page.mouse.move(from.x, from.y)
    await page.mouse.down()
    await page.mouse.move(from.x + dx, from.y + dy, { steps: 8 })
    await page.mouse.up()
  }

  // A drag marks it and shows the bar; Discard puts it back.
  const start = await centre()
  await drag(160, 90)
  await expect(page.getByText('1 responsibility moved')).toBeVisible()
  await expect(node).toHaveAttribute('data-unsaved', 'true')
  await page.getByRole('button', { name: 'Discard' }).click()
  await expect.poll(async () => near(await centre(), start)).toBe(true)

  // Leaving the tab with a move asks first.
  await node.focus()
  await page.keyboard.press('Shift+ArrowDown')
  await expect(page.getByText('1 responsibility moved')).toBeVisible()
  await page.getByRole('link', { name: 'Links', exact: true }).click()
  await expect(page.getByText('Leave without saving?')).toBeVisible()
  await page.getByRole('button', { name: 'Cancel' }).click()

  // Saved: the moved one and the others where they are; after a reload, the same picture.
  await drag(-140, 60)
  await page.getByRole('button', { name: 'Save layout' }).click()
  await expect(page.getByText('Saved the places of 3 responsibilities')).toBeVisible()
  await expect(page.getByText('1 responsibility moved')).toBeHidden()
  await page.getByRole('button', { name: 'Fit' }).click()
  const saved = await onMap()
  await page.reload()
  await expect(node).toBeVisible()
  await expect.poll(async () => near(await onMap(), saved)).toBe(true)

  // The panel: its people and place; back to automatic.
  await node.click()
  const panel = page.getByRole('complementary', { name: 'Details' })
  await expect(panel.getByRole('heading', { name: 'Network' })).toBeVisible()
  await expect(panel.locator('[data-test="overview-place"]')).toContainText('Saved')
  await shot(page, 'responsibilities-overview-panel')
  await panel.locator('[data-test="overview-automatic"]').click()
  await expect(page.getByText('Network is placed automatically again')).toBeVisible()
  await expect(panel.locator('[data-test="overview-place"]')).toContainText('Automatic')
  await panel.getByRole('button', { name: 'Close' }).click()

  // Everyone back to automatic, as the seed had it.
  await page.getByRole('button', { name: 'Layout' }).click()
  await page.getByRole('menuitem', { name: 'Back to automatic for all responsibilities…' }).click()
  await page.getByRole('button', { name: 'Back to automatic', exact: true }).click()
  await expect(page.getByText('Every responsibility is placed automatically again')).toBeVisible()
  expect(problems).toEqual([])
})

test('notes on the overview: added, changed, moved with the layout, and deleted', async ({ page }) => {
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto(`${list}/overview`)
  const map = page.getByTestId('responsibilities-map')
  // The seed's notes, with the people: the default layers.
  await expect(map.getByRole('button', { name: /^Note Infra team/ })).toBeVisible()

  await page.locator('[data-test="overview-new-note"]').click()
  const dialog = page.getByRole('dialog', { name: 'New note' })
  await dialog.locator('[data-test="note-text"]').fill('**Call the ISP** before 9:00 when the WAN is down')
  await dialog.getByRole('button', { name: 'Green' }).click()
  await dialog.getByRole('group', { name: 'Width' }).getByRole('button', { name: 'Large' }).click()
  await dialog.locator('[data-test="note-save"]').click()
  await expect(page.getByText('Note added')).toBeVisible()
  const note = map.getByRole('button', { name: /^Note Call the ISP/ })
  await expect(note).toBeVisible()
  const panel = page.getByRole('complementary', { name: 'Details' })
  await expect(panel.getByText('Call the ISP')).toBeVisible()
  await shot(page, 'responsibilities-overview-note')

  // Changed in its dialog.
  await panel.locator('[data-test="note-edit"]').click()
  const edit = page.getByRole('dialog', { name: 'Edit note' })
  await edit.locator('[data-test="note-text"]').fill('**Call the ISP** before 8:30 when the WAN is down')
  await edit.locator('[data-test="note-save"]').click()
  await expect(page.getByText('Note saved')).toBeVisible()
  await expect(panel.getByText('before 8:30')).toBeVisible()
  await panel.getByRole('button', { name: 'Close' }).click()

  // Moved: saved with the layout, and in its place after a reload.
  const paper = note.locator('rect.paper')
  const box = async () => {
    const b = (await paper.boundingBox())!
    return { x: Math.round(b.x), y: Math.round(b.y) }
  }
  // Where it is on the map, whatever the page's scroll.
  const onMap = async () => {
    const b = await box()
    const m = (await map.boundingBox())!
    return { x: b.x - Math.round(m.x), y: b.y - Math.round(m.y) }
  }
  const from = await box()
  await page.mouse.move(from.x + 20, from.y + 20)
  await page.mouse.down()
  await page.mouse.move(from.x - 180, from.y + 120, { steps: 8 })
  await page.mouse.up()
  await expect(page.getByText('1 note moved')).toBeVisible()
  await expect(note).toHaveAttribute('data-unsaved', 'true')
  await page.getByRole('button', { name: 'Save layout' }).click()
  await expect(page.getByText('Saved the places of 3 responsibilities and 1 note')).toBeVisible()
  // The bar goes once the map has the saved places, and people are laid out around the note's new one.
  await expect(page.getByText('1 note moved')).toBeHidden()
  await page.getByRole('button', { name: 'Fit' }).click()
  const moved = await onMap()
  await page.reload()
  await expect(note).toBeVisible()
  await expect
    .poll(async () => {
      const now = await onMap()
      return Math.abs(now.x - moved.x) <= 1 && Math.abs(now.y - moved.y) <= 1
    })
    .toBe(true)

  // Deleted, and the layout back to automatic, as the seed had it.
  await note.click()
  await panel.locator('[data-test="note-delete"]').click()
  await page.getByRole('alertdialog', { name: 'Delete the note?' }).getByRole('button', { name: 'Delete' }).click()
  await expect(page.getByText('Note deleted')).toBeVisible()
  await expect(note).toBeHidden()
  await page.getByRole('button', { name: 'Layout' }).click()
  await page.getByRole('menuitem', { name: 'Back to automatic for all responsibilities…' }).click()
  await page.getByRole('button', { name: 'Back to automatic', exact: true }).click()
  await expect(page.getByText('Every responsibility is placed automatically again')).toBeVisible()
  expect(problems).toEqual([])
})
