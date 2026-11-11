<script setup lang="ts">
import { computed } from 'vue'

/**
 * A status code with its meaning, "460 Heartbeat missing" (§5.3), coloured by range: up to 399 is
 * success, from 400 a failure. Codes below 100 (v0.12's −1 for "never reported") are neutral.
 */
const props = defineProps<{ code: number | null | undefined; name?: string | null; compact?: boolean }>()

const tone = computed(() => {
  const code = props.code
  if (code === null || code === undefined || code < 100) {
    return 'none'
  }
  return code <= 399 ? 'ok' : 'fail'
})
</script>

<template>
  <span class="code-tag" :class="`tone-${tone}`" :title="compact && name ? `${code} ${name}` : undefined">
    <span class="code">{{ code ?? '–' }}</span>
    <span v-if="name && !compact" class="name ellipsis">{{ name }}</span>
  </span>
</template>

<style scoped>
.code-tag {
  display: inline-flex;
  align-items: baseline;
  gap: 0.35rem;
  max-width: 100%;
  min-width: 0;
}

.code {
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  font-weight: 600;
  padding: 0.05rem 0.35rem;
  border-radius: var(--p-border-radius-sm);
  line-height: 1.4;
}

.tone-ok .code {
  color: var(--pnmt-ok);
  background: var(--pnmt-ok-soft);
}

.tone-fail .code {
  color: var(--pnmt-fail);
  background: var(--pnmt-fail-soft);
}

.tone-none .code {
  color: var(--pnmt-muted);
  background: var(--pnmt-none-soft);
}

.name {
  color: var(--pnmt-text);
}
</style>
