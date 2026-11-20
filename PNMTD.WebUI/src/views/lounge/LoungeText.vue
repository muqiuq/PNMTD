<script setup lang="ts">
import { computed } from 'vue'
import { useSession } from '@/stores/session'
import { segments, type Segment } from './loungeFormat'

/**
 * A line's text (docs/lounge-plan.md §15.2): cases, hosts, sensors and tickets as chips, linked where the reader's rights
 * reach them; the rest as written.
 */
const props = defineProps<{ text: string }>()
const session = useSession()
const parts = computed(() => segments(props.text))

function link(s: Segment) {
  switch (s.kind) {
    case 'case':
      return session.can('Cookie', 'Use') ? { name: 'cookie', params: { tab: 'cases' }, query: { case: s.id } } : null
    case 'host':
      return session.can('Monitoring', 'View') ? { name: 'host', params: { id: s.id } } : null
    case 'sensor':
      return session.can('Monitoring', 'View') ? { name: 'sensor', params: { id: s.id } } : null
    case 'ticket':
      return session.can('Redmine', 'View') ? { name: 'redmine-issue', params: { id: s.id } } : null
    default:
      return null
  }
}
const icon = (s: Segment) =>
  s.kind === 'case'
    ? 'pi pi-briefcase'
    : s.kind === 'host'
      ? 'pi pi-server'
      : s.kind === 'sensor'
        ? 'pi pi-gauge'
        : 'pi pi-ticket'
</script>

<template>
  <span class="lounge-text"
    ><template v-for="(s, i) in parts" :key="i"
      ><template v-if="s.kind === 'text'">{{ s.text }}</template
      ><RouterLink v-else-if="link(s)" :to="link(s)!" class="chip"
        ><i :class="icon(s)" aria-hidden="true" /> {{ s.text }}</RouterLink
      ><span v-else class="chip"><i :class="icon(s)" aria-hidden="true" /> {{ s.text }}</span></template
    ></span
  >
</template>

<style scoped>
.lounge-text {
  white-space: pre-wrap;
  word-break: break-word;
}

.chip {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 0 6px;
  border-radius: 999px;
  border: 1px solid var(--pnmt-border);
  background: var(--pnmt-none-soft);
  font-size: 0.857em;
  text-decoration: none;
  white-space: nowrap;
}
</style>
