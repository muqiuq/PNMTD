import { ApiError } from '@/api/client'
import { api, type S } from '@/api/resources'
import { decryptBackup, fromBase64, gunzip } from '@/composables/useBackupKey'
import { compare, unified } from './lineDiff'

/**
 * Reading backups PNMTD may not be able to read itself (docs/mikrotik-backup-plan.md §3.6): an encrypted version is
 * decrypted with the key loaded in this tab, and compared here with the same diff as the server's.
 */

/** Whether showing these versions needs the key loaded in the tab. */
export const needsKey = (...items: (S['MikrotikBackupItem'] | null | undefined)[]) =>
  items.some((i) => !!i?.encryptedWith)

/** An export's text: as the server sends it, or decrypted here. */
export async function exportText(
  sensorId: string,
  backupId: string,
): Promise<{ item: S['MikrotikBackupItem']; text: string }> {
  const c = await api.mikrotik.backup(sensorId, backupId)
  if (typeof c.content === 'string') return { item: c.backup, text: c.content }
  return { item: c.backup, text: await gunzip(await decryptBackup(fromBase64(c.ciphertext!))) }
}

/** A stored file, decrypted here: an export's text, or a binary backup as RouterOS wrote it. */
export async function decryptedFile(sensorId: string, item: S['MikrotikBackupItem']): Promise<Blob> {
  if (item.kind === 'Export') {
    return new Blob([(await exportText(sensorId, item.id)).text], { type: 'text/plain' })
  }
  const response = await fetch(api.mikrotik.downloadUrl(sensorId, item.id), { credentials: 'same-origin' })
  if (!response.ok) throw new Error(`The download failed (${response.status}).`)
  return new Blob([(await decryptBackup(new Uint8Array(await response.arrayBuffer()))) as BlobPart])
}

/**
 * The changes to a version from `againstId`, or from the export before it: the server's diff, or, when either is
 * encrypted (409 backup_encrypted), the same diff made here.
 */
export async function backupDiff(sensorId: string, toId: string, againstId?: string): Promise<S['MikrotikBackupDiff']> {
  try {
    return await api.mikrotik.diff(sensorId, toId, againstId)
  } catch (e) {
    if (!(e instanceof ApiError && e.code === 'backup_encrypted')) throw e
  }
  const to = await exportText(sensorId, toId)
  const fromId = againstId ?? (await exportBefore(sensorId, to.item.number))?.id
  const from = fromId ? await exportText(sensorId, fromId) : null
  const diff = compare(from?.text ?? '', to.text)
  return {
    from: from?.item ?? null,
    to: to.item,
    ...diff,
    unified: unified(diff, from ? `v${from.item.number}.rsc` : '/dev/null', `v${to.item.number}.rsc`),
  }
}

/** The newest export before a version. */
async function exportBefore(sensorId: string, number: number): Promise<S['MikrotikBackupItem'] | null> {
  for (let page = 1; ; page++) {
    const p = await api.mikrotik.backups(sensorId, { kind: 'Export', page, pageSize: 100 })
    const hit = p.items.find((i) => i.number < number)
    if (hit) return hit
    if (page * p.pageSize >= p.totalCount) return null
  }
}

/** Saves a blob as a file, as a download link would. */
export function saveFile(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = name
  a.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}
