<script setup lang="ts">
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import EmptyState from '@/components/EmptyState.vue'

// Stands in for the pages of phase 6 (docs/webui-migration-plan.md §7) until each one exists.
const route = useRoute()
</script>

<template>
  <div class="page">
    <PageHeader :title="String(route.meta.title ?? '')" />
    <div class="panel">
      <EmptyState
        icon="pi pi-wrench"
        title="Not built yet"
        text="This page arrives with the feature pages (phase 6)."
      />
    </div>
  </div>
</template>
