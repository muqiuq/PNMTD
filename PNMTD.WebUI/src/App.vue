<script setup lang="ts">
import Button from 'primevue/button'
import Toast from 'primevue/toast'
import ConfirmDialog from 'primevue/confirmdialog'
import { useRoute } from 'vue-router'
import AppLayout from '@/layouts/AppLayout.vue'
import AuthLayout from '@/layouts/AuthLayout.vue'
import { useUndoButton } from '@/composables/useUndo'
import { usePreferences } from '@/stores/preferences'

// Applies the theme before the first page renders.
usePreferences()
const route = useRoute()
const undo = useUndoButton()
</script>

<template>
  <!-- Nothing until the first navigation is resolved (the guard loads the session). -->
  <template v-if="route.matched.length">
    <AuthLayout v-if="route.meta.layout === 'auth'"><RouterView /></AuthLayout>
    <AppLayout v-else><RouterView /></AppLayout>
  </template>
  <Toast position="bottom-right" />
  <Toast position="bottom-right" group="undo">
    <template #message="{ message }">
      <div class="undo-toast">
        <i class="pi pi-info-circle" aria-hidden="true" />
        <span class="text">{{ message.summary }}</span>
        <Button label="Undo" size="small" text @click="undo(message)" />
      </div>
    </template>
  </Toast>
  <ConfirmDialog />
</template>

<style scoped>
.undo-toast {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  width: 100%;
}

.undo-toast .text {
  flex: 1;
}
</style>
