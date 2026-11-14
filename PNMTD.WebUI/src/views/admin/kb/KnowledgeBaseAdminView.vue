<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import FilesTab from './FilesTab.vue'
import TagsTab from './TagsTab.vue'

/**
 * Admin → Knowledge base (docs/locations-knowledge-plan.md §10.6): the tags' vocabulary (rename, merge, delete) and the
 * files (totals and the folder's disk, the File storage check's findings, the largest, unused and unknown files,
 * deleting at once). Admin.
 */
const route = useRoute()
const tab = computed(() => (route.params.tab === 'files' ? 'files' : 'tags'))
const to = (t: string) => ({ name: 'kb-admin', params: { tab: t } })
const tabs = [
  { key: 'tags', label: 'Tags', icon: 'pi pi-tags', to: to('tags') },
  { key: 'files', label: 'Files', icon: 'pi pi-folder', to: to('files') },
]
</script>

<template>
  <div class="page">
    <PageHeader title="Knowledge base" :crumbs="[{ label: 'Admin' }]" />
    <TabNav :items="tabs" :active="tab" label="Knowledge base" />
    <FilesTab v-if="tab === 'files'" />
    <TagsTab v-else />
  </div>
</template>
