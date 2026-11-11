import { useToast } from 'primevue/usetoast'
import { ApiError } from '@/api/client'

/** Toasts with one voice: success briefly, errors until read (§5.7). */
export function useNotify() {
  const toast = useToast()
  return {
    success(summary: string, detail?: string) {
      toast.add({ severity: 'success', summary, detail, life: 3000 })
    },
    info(summary: string, detail?: string) {
      toast.add({ severity: 'info', summary, detail, life: 5000 })
    },
    error(error: unknown, summary = 'Something went wrong') {
      const detail =
        error instanceof ApiError
          ? error.code === 'forbidden_role'
            ? 'Not allowed for your role.'
            : error.message
          : error instanceof Error
            ? error.message
            : String(error)
      toast.add({ severity: 'error', summary, detail, life: 8000 })
    },
  }
}
