<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import { downloadText } from '@/format/markdown'

/**
 * *Documentation* (docs/connectors-plan.md §13.1): docs/connectors-api.md, the reference for the callers' developers, as the server
 * embeds it, and as a .md file to hand on.
 */
const docs = useQuery({ queryKey: [...keys.connectors, 'docs'], queryFn: api.connectors.docs, staleTime: Infinity })
// The page's header names it already: the document's own title goes.
const source = computed(() => (docs.data.value?.markdown ?? '').replace(/^# .*\n+/, ''))

/** The file stands alone: the document as it is, its title included. */
function download() {
  if (docs.data.value) downloadText('pnmt-connectors-api.md', docs.data.value.markdown)
}
</script>

<template>
  <ErrorState v-if="docs.isError.value" :error="docs.error.value" what="the documentation" @retry="docs.refetch()" />
  <section v-else-if="docs.data.value" class="panel panel-body docs">
    <div class="toolbar">
      <Button
        label="Download as Markdown"
        icon="pi pi-download"
        size="small"
        severity="secondary"
        outlined
        title="This documentation as a .md file, for the callers' developers"
        @click="download"
      />
    </div>
    <MarkdownView :source="source" />
  </section>
</template>

<style scoped>
.docs {
  max-width: 64rem;
}
.toolbar {
  display: flex;
  justify-content: flex-end;
}
</style>
