import type { MenuItem } from 'primevue/menuitem'
import { ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import type { RemovalSource } from './sshKeyFormat'

/**
 * A registered key's actions (docs/linux-sensor-plan.md §6.4, §11.4), for the Keys tab's rows and the key's drawer: Edit…,
 * Retire… (then Remove from every server… for the places it is still in), Reactivate, Remove from servers…, Delete…. The page
 * renders EditKeyDialog on `editing` and RemoveKeysDialog on `removing`.
 */
export function useKeyActions(on: { changed?: (d: S['SshKeyDetail']) => void; deleted?: () => void } = {}) {
  const editing = ref<S['SshKeyItem'] | null>(null)
  const removing = ref<RemovalSource | null>(null)
  const { run, done, ask } = useActions()
  const invalidate = [keys.sshKeys, keys.agents]

  async function retire(k: S['SshKeyItem']) {
    const where = k.servers
      ? ` It is still authorized on ${k.servers} ${k.servers === 1 ? 'server' : 'servers'}: removing it there comes next.`
      : ''
    if (!(await ask(`Retire ${k.name}?`, `If it appears again anywhere, 465 says so.${where}`, 'Retire'))) return
    const d = await run(() => api.sshKeys.retire(k.id), { success: `${k.name} retired`, invalidate })
    if (!d) return
    on.changed?.(d)
    if (d.authorizations.length) removing.value = { kind: 'key', keyId: k.id, name: k.name, reason: 'Retirement' }
  }

  async function reactivate(k: S['SshKeyItem']) {
    const d = await run(() => api.sshKeys.reactivate(k.id), { success: `${k.name} active again`, invalidate })
    if (d) on.changed?.(d)
  }

  async function remove(k: S['SshKeyItem']) {
    if (
      !(await ask(
        `Delete the registration of ${k.name}?`,
        'The key becomes unregistered again: it stays authorized wherever it is, and where it appears anew, 465 says so.',
      ))
    )
      return
    if (await done(() => api.sshKeys.remove(k.id), { success: `${k.name} is unregistered now`, invalidate }))
      on.deleted?.()
  }

  const removeFromServers = (k: S['SshKeyItem']) =>
    (removing.value = { kind: 'key', keyId: k.id, name: k.name, reason: k.state === 'Retired' ? 'Retirement' : 'Key' })

  const menu = (k: S['SshKeyItem']): MenuItem[] => [
    { label: 'Edit…', icon: 'pi pi-pencil', command: () => (editing.value = k) },
    k.state === 'Active'
      ? { label: 'Retire…', icon: 'pi pi-ban', command: () => void retire(k) }
      : { label: 'Reactivate', icon: 'pi pi-replay', command: () => void reactivate(k) },
    {
      label: 'Remove from servers…',
      icon: 'pi pi-trash',
      disabled: !k.servers,
      command: () => removeFromServers(k),
    },
    { separator: true },
    { label: 'Delete…', icon: 'pi pi-times', command: () => void remove(k) },
  ]

  return { editing, removing, retire, reactivate, remove, removeFromServers, menu }
}
