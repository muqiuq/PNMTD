<script setup lang="ts">
/** One field of the item's form (docs/licences-plan.md §8.7): its label, the input, and the server's reason or a hint under it. */
defineProps<{ label: string; for: string; error?: string; hint?: string; wide?: boolean; required?: boolean }>()
</script>

<template>
  <div class="field" :class="{ wide }">
    <label :for="$props.for">{{ label }}<span v-if="required" class="required" aria-hidden="true"> *</span></label>
    <slot />
    <small v-if="error" class="field-error">{{ error }}</small>
    <small v-else-if="hint" class="field-hint">{{ hint }}</small>
  </div>
</template>

<style scoped>
.required {
  color: var(--pnmt-muted);
}
</style>
