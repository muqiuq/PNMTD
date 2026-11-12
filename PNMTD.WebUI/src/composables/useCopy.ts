import { useNotify } from './useNotify'

/** Copies text to the clipboard with a toast, as CopyField does, for buttons that copy something not shown as is. */
export function useCopy() {
  const notify = useNotify()
  return async (text: string, label?: string) => {
    try {
      await navigator.clipboard.writeText(text)
      notify.success('Copied', label)
    } catch {
      notify.error(new Error('The browser does not allow copying here; select the text instead.'), 'Not copied')
    }
  }
}
