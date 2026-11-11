<script setup lang="ts">
import { computed } from 'vue'
import { useNow } from '@/composables/useNow'
import { formatAbsolute, formatRelative, toDate } from '@/format'

/** "5 min ago", updating itself, with the absolute time as a tooltip (§5.7). */
const props = defineProps<{ value: string | null | undefined; empty?: string }>()

const now = useNow()
const date = computed(() => toDate(props.value))
const text = computed(() => (date.value ? formatRelative(date.value, now.value) : (props.empty ?? 'never')))
</script>

<template>
  <time v-if="date" class="relative-time nowrap" :datetime="date.toISOString()" :title="formatAbsolute(date)">{{
    text
  }}</time>
  <span v-else class="relative-time muted">{{ text }}</span>
</template>
