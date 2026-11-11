<script setup lang="ts">
import PageHeader from '@/components/PageHeader.vue'
import EmptyState from '@/components/EmptyState.vue'
</script>

<template>
  <div class="page">
    <PageHeader title="Not found" />
    <div class="panel">
      <EmptyState
        icon="pi pi-compass"
        title="There is no such page."
        text="It may have moved, or the link is incomplete."
      >
        <RouterLink to="/">Go to the dashboard</RouterLink>
      </EmptyState>
    </div>
  </div>
</template>
