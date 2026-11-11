import { useToast } from 'primevue/usetoast'
import type { ToastMessageOptions } from 'primevue/toast'

const actions = new Map<string, () => Promise<unknown> | void>()
let next = 0

/** A toast with an Undo button (App.vue renders the group "undo"). */
export function useUndo() {
  const toast = useToast()
  return (summary: string, undo: () => Promise<unknown> | void) => {
    const id = `undo-${++next}`
    actions.set(id, undo)
    toast.add({ severity: 'info', summary, detail: id, group: 'undo', life: 8000 })
  }
}

/** The Undo button of a toast: runs its action once, then closes it. */
export function useUndoButton() {
  const toast = useToast()
  return (message: ToastMessageOptions) => {
    const id = String(message.detail ?? '')
    const action = actions.get(id)
    actions.delete(id)
    toast.remove(message)
    void action?.()
  }
}
