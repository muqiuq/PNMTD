<script setup lang="ts">
defineProps<{ icon?: string; title: string; text?: string }>()
</script>

<template>
  <div class="empty-state">
    <i :class="icon ?? 'pi pi-inbox'" aria-hidden="true" />
    <div>
      <div class="title">{{ title }}</div>
      <div v-if="text" class="muted">{{ text }}</div>
      <div v-if="$slots.default" class="action"><slot /></div>
    </div>
  </div>
</template>

<style scoped>
.empty-state {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
  padding: var(--pnmt-space-4);
  color: var(--pnmt-text);
}

.empty-state > i {
  font-size: 1.5rem;
  color: var(--pnmt-muted);
}

.title {
  font-weight: 500;
}

.action {
  margin-top: var(--pnmt-space-2);
}
</style>
