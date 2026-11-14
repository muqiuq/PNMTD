<script setup lang="ts">
import { computed } from 'vue'
import { fileIcon, fileSize } from './files'
import type { MarkdownFile } from './types'

/**
 * A link to an attachment in a page (docs/locations-knowledge-plan.md §5.6): an icon by type, the name
 * and the size; a click downloads (the server always sends attachments as downloads, §5.5). Without
 * the file's metadata it shows the link's text only; a file an Admin removed shows as such.
 */
const props = defineProps<{
  href: string
  /** The link's text in the Markdown. */
  name: string
  /** The file's metadata from the page's response; undefined while unknown. */
  file?: MarkdownFile | null
  /** Known to be gone: not among the page's files. */
  removed?: boolean
}>()

const shownName = computed(() => props.name.trim() || props.file?.name || 'Attachment')
const icon = computed(() => fileIcon(props.file?.name ?? props.name, props.file?.contentType))
const gone = computed(() => props.removed || props.file?.deleted === true)
const label = computed(() => `Download ${shownName.value}${props.file ? `, ${fileSize(props.file.size)}` : ''}`)
</script>

<template>
  <span v-if="gone" class="attachment-chip removed" :title="`${shownName}: removed`">
    <i class="pi pi-ban" aria-hidden="true" />
    <span class="name">{{ shownName }}</span>
    <span class="meta">File removed</span>
  </span>
  <a v-else class="attachment-chip" :href="href" download :title="label" :aria-label="label">
    <i :class="icon" aria-hidden="true" />
    <span class="name">{{ shownName }}</span>
    <span v-if="file" class="meta">{{ fileSize(file.size) }}</span>
    <i class="pi pi-download download" aria-hidden="true" />
  </a>
</template>

<style scoped>
.attachment-chip {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  max-width: 100%;
  padding: 1px var(--pnmt-space-2);
  border: 1px solid var(--pnmt-border);
  border-radius: 999px;
  background: var(--pnmt-bg);
  color: var(--pnmt-text);
  vertical-align: baseline;
  line-height: 1.6;
}

a.attachment-chip:hover {
  border-color: var(--p-primary-color);
  text-decoration: none;
}

.attachment-chip i {
  color: var(--pnmt-muted);
  font-size: 0.9rem;
}

.name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.meta {
  color: var(--pnmt-muted);
  font-size: 0.857rem;
  white-space: nowrap;
}

.removed {
  color: var(--pnmt-muted);
  border-style: dashed;
}

@media print {
  .download {
    display: none;
  }
}
</style>
