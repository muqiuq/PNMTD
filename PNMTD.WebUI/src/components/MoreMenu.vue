<script setup lang="ts">
import Button from 'primevue/button'
import Menu from 'primevue/menu'
import type { MenuItem } from 'primevue/menuitem'
import { ref } from 'vue'

/** The ⋯ menu: destructive and rare actions live here, never as the first button (§5.3). */
defineProps<{ items: MenuItem[]; label?: string }>()
const menu = ref<InstanceType<typeof Menu> | null>(null)
const id = `more-${Math.random().toString(36).slice(2, 8)}`
</script>

<template>
  <Button
    icon="pi pi-ellipsis-h"
    text
    rounded
    severity="secondary"
    :aria-label="label ?? 'More actions'"
    aria-haspopup="true"
    :aria-controls="id"
    @click="(e: Event) => menu?.toggle(e)"
  />
  <Menu :id="id" ref="menu" :model="items" popup />
</template>
