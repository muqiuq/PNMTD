<script setup lang="ts">
import { computed } from 'vue'
import { useNow } from '@/composables/useNow'
import { formatAbsolute, formatDuration, toDate } from '@/format'

/** "2 h 14 min": how long something has been so (failing since), updating itself. */
const props = defineProps<{ since: string | null | undefined; prefix?: string }>()
const now = useNow()
const date = computed(() => toDate(props.since))
</script>

<template>
  <span v-if="date" class="nowrap" :title="`${prefix ?? 'Since'} ${formatAbsolute(date)}`"
    >{{ prefix ? `${prefix} ` : '' }}{{ formatDuration(now - date.getTime()) }}</span
  >
  <span v-else class="muted">–</span>
</template>
