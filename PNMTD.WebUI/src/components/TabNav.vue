<script setup lang="ts">
import type { RouteLocationRaw } from 'vue-router'

/**
 * A page's subpages as tabs, each a link with its own route (§6.2), so a tab can be linked and opened anew. A badge in the
 * danger tone is red: a server's OS updates with a high-risk update or a failure (docs/linux-os-updates-plan.md §10.1).
 */
defineProps<{
  items: {
    key: string
    label: string
    icon?: string
    to: RouteLocationRaw
    badge?: string | number | null
    tone?: 'danger' | null
  }[]
  active: string
  label: string
  /** Many tabs (Admin → Cookie): a second line instead of scrolling, where the tabs past the edge would be hidden. */
  wrap?: boolean
}>()
</script>

<template>
  <nav class="tab-nav" :class="{ wrap }" :aria-label="label">
    <RouterLink
      v-for="item in items"
      :key="item.key"
      :to="item.to"
      class="tab"
      :class="{ active: item.key === active }"
      :aria-current="item.key === active ? 'page' : undefined"
    >
      <i v-if="item.icon" :class="item.icon" aria-hidden="true" />
      <span>{{ item.label }}</span>
      <span v-if="item.badge" class="badge" :class="item.tone ? `tone-${item.tone}` : undefined">{{ item.badge }}</span>
    </RouterLink>
  </nav>
</template>

<style scoped>
.tab-nav {
  display: flex;
  gap: var(--pnmt-space-1);
  border-bottom: 1px solid var(--pnmt-border);
  overflow-x: auto;
  scrollbar-width: none;
}

.tab-nav.wrap {
  flex-wrap: wrap;
  overflow-x: visible;
}

.tab {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  color: var(--pnmt-muted);
  font-weight: 500;
  white-space: nowrap;
  border-bottom: 2px solid transparent;
  margin-bottom: -1px;
}

.tab:hover {
  color: var(--pnmt-text);
  text-decoration: none;
}

.tab.active {
  color: var(--p-primary-color);
  border-bottom-color: var(--p-primary-color);
}

.tab i {
  font-size: 0.9rem;
}

.badge {
  font-size: 0.786rem;
  font-weight: 600;
  padding: 0 0.4rem;
  border-radius: 999px;
  background: var(--pnmt-none-soft);
  color: var(--pnmt-text);
}

.badge.tone-danger {
  background: var(--pnmt-fail-soft);
  color: var(--pnmt-fail);
}
</style>
