import { readCookie } from '@/api/client'
import type { BoardImageItem } from '@/api/boards'
import { uploadFailure } from '@/components/markdown/useUpload'

/**
 * An image for a board's image element (docs/board-messages-plan.md §9.4): POST /api/v1/boards/{id}/images with the file as the body,
 * as the knowledge base's uploads, by XMLHttpRequest for its progress. The server checks it by its bytes and keeps animations.
 */
export const boardImageTypes = ['image/png', 'image/jpeg', 'image/gif', 'image/webp']

export function uploadBoardImage(
  boardId: string,
  file: Blob,
  name: string,
  onProgress?: (fraction: number) => void,
): Promise<BoardImageItem> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    xhr.open('POST', `/api/v1/boards/${boardId}/images?name=${encodeURIComponent(name)}`)
    xhr.withCredentials = true
    xhr.setRequestHeader('Accept', 'application/json')
    xhr.setRequestHeader('Content-Type', file.type || 'application/octet-stream')
    const token = readCookie('XSRF-TOKEN')
    if (token) xhr.setRequestHeader('X-XSRF-TOKEN', token)
    xhr.upload.onprogress = (event) => {
      if (event.lengthComputable && event.total > 0) onProgress?.(event.loaded / event.total)
    }
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        try {
          resolve(JSON.parse(xhr.responseText) as BoardImageItem)
          return
        } catch {
          // Not PNMT's answer: below.
        }
      }
      reject(uploadFailure(xhr.status, xhr.responseText, name))
    }
    xhr.onerror = () => reject(uploadFailure(0, '', name))
    xhr.send(file)
  })
}
