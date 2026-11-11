<script setup lang="ts">
import Toast from 'primevue/toast'
import ConfirmDialog from 'primevue/confirmdialog'
import { useRoute } from 'vue-router'
import AppLayout from '@/layouts/AppLayout.vue'
import AuthLayout from '@/layouts/AuthLayout.vue'
import { usePreferences } from '@/stores/preferences'

// Applies the theme before the first page renders.
usePreferences()
const route = useRoute()
</script>

<template>
  <!-- Nothing until the first navigation is resolved (the guard loads the session). -->
  <template v-if="route.matched.length">
    <AuthLayout v-if="route.meta.layout === 'auth'"><RouterView /></AuthLayout>
    <AppLayout v-else><RouterView /></AppLayout>
  </template>
  <Toast position="bottom-right" />
  <ConfirmDialog />
</template>
