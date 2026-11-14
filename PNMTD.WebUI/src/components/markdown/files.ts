/** Small helpers about files, shared by the chip, the uploads and the editors (docs/locations-knowledge-plan.md §5.5). */

/** "2.4 MB", "830 KB": as the limits are written (§5.5; 1 MB = 1 MiB, as `KnowledgeBase:MaxImageBytes`). */
export function fileSize(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`
  }
  if (bytes < 1024 * 1024) {
    return `${Math.round(bytes / 1024)} KB`
  }
  const mb = bytes / (1024 * 1024)
  return Number.isInteger(mb) ? `${mb} MB` : `${mb.toFixed(1)} MB`
}

/** The lower-case extension of a file name, without the dot ("" without one). */
export function extensionOf(name: string): string {
  const dot = name.lastIndexOf('.')
  return dot > 0 ? name.slice(dot + 1).toLowerCase() : ''
}

/** PrimeIcons by kind of file. */
export function fileIcon(name: string, contentType = ''): string {
  const extension = extensionOf(name)
  if (
    contentType.startsWith('image/') ||
    ['png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'heic', 'avif'].includes(extension)
  ) {
    return 'pi pi-image'
  }
  if (extension === 'pdf' || contentType === 'application/pdf') {
    return 'pi pi-file-pdf'
  }
  if (['doc', 'docx', 'odt', 'rtf'].includes(extension)) {
    return 'pi pi-file-word'
  }
  if (['xls', 'xlsx', 'ods', 'csv'].includes(extension)) {
    return 'pi pi-file-excel'
  }
  if (['zip', 'tar', 'gz', 'tgz', '7z', 'rar', 'xz', 'bz2'].includes(extension)) {
    return 'pi pi-box'
  }
  if (
    ['txt', 'log', 'md', 'conf', 'cfg', 'ini', 'yaml', 'yml', 'json', 'xml', 'rsc', 'backup', 'toml'].includes(
      extension,
    )
  ) {
    return 'pi pi-file-edit'
  }
  return 'pi pi-file'
}

/** Text in Markdown's link text or image alt: brackets and backslashes escaped, one line. */
export function linkText(text: string): string {
  return text.replace(/[\r\n]+/g, ' ').replace(/([\\[\]])/g, '\\$1')
}

/** The Markdown for an uploaded file (§5.1): `![name](url)` for an image, `[name](url)` otherwise. */
export function fileMarkdown(file: { kind: string; name: string; url: string }): string {
  const label = linkText(file.name)
  return file.kind.toLowerCase() === 'image' ? `![${label}](${file.url})` : `[${label}](${file.url})`
}
