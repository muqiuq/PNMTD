<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import MarkdownView from '@/components/markdown/MarkdownView.vue'

/** *Documentation* (docs/connectors-plan.md §13.1): docs/connectors-api.md, the reference for the callers' developers, as the server embeds it. */
const docs = useQuery({ queryKey: [...keys.connectors, 'docs'], queryFn: api.connectors.docs, staleTime: Infinity })
// The page's header names it already: the document's own title goes.
const source = computed(() => (docs.data.value?.markdown ?? '').replace(/^# .*\n+/, ''))
</script>

<template>
  <ErrorState v-if="docs.isError.value" :error="docs.error.value" what="the documentation" @retry="docs.refetch()" />
  <section v-else-if="docs.data.value" class="panel panel-body docs">
    <MarkdownView :source="source" />
  </section>
</template>

<style scoped>
.docs {
  max-width: 64rem;
}
</style>
