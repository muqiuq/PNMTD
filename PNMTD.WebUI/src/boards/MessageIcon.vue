<script setup lang="ts">
import type { BoardMessageKind } from './messages'

/** A kind's icon (docs/board-messages-plan.md §4.1): five inline SVGs, as the board app has no icon font. */
defineProps<{ kind: BoardMessageKind }>()
</script>

<template>
  <svg class="board-msg-icon" viewBox="0 0 48 48" aria-hidden="true" focusable="false">
    <template v-if="kind === 'Alert'">
      <path d="M16 3h16l13 13v16L32 45H16L3 32V16Z" fill="currentColor" />
      <path d="M24 12v15" stroke="var(--board-msg-icon-on, #fff)" stroke-width="5" stroke-linecap="round" />
      <circle cx="24" cy="35" r="3" fill="var(--board-msg-icon-on, #fff)" />
    </template>
    <template v-else-if="kind === 'Warning'">
      <path d="M24 4 46 43H2Z" fill="currentColor" stroke-linejoin="round" />
      <path d="M24 17v12" stroke="var(--board-msg-icon-on, #000)" stroke-width="5" stroke-linecap="round" />
      <circle cx="24" cy="36" r="3" fill="var(--board-msg-icon-on, #000)" />
    </template>
    <template v-else-if="kind === 'Maintenance'">
      <path
        d="M30 4a11 11 0 0 0-10 15L5 34a4 4 0 0 0 0 6l3 3a4 4 0 0 0 6 0l15-15A11 11 0 0 0 44 18l-7 7-6-2-2-6 7-7a11 11 0 0 0-6-6Z"
        fill="currentColor"
      />
    </template>
    <template v-else-if="kind === 'Information'">
      <circle cx="24" cy="24" r="21" fill="currentColor" />
      <circle cx="24" cy="14" r="3" fill="var(--board-msg-icon-on, #fff)" />
      <path d="M24 21v15" stroke="var(--board-msg-icon-on, #fff)" stroke-width="5" stroke-linecap="round" />
    </template>
    <template v-else>
      <circle cx="24" cy="24" r="21" fill="currentColor" />
      <path
        d="m13 25 7 7 15-15"
        stroke="var(--board-msg-icon-on, #fff)"
        stroke-width="5"
        fill="none"
        stroke-linecap="round"
        stroke-linejoin="round"
      />
    </template>
  </svg>
</template>
