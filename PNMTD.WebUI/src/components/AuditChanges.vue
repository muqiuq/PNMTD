<script setup lang="ts">
import { computed } from 'vue'
import type { S } from '@/api/resources'

/**
 * An audit entry's changes, field by field (docs/audit-redmine-zabbix-plan.md §3.4, §4.2): before and after; "changed"
 * for a secret, which is never stored; the lines added and removed for a long text. Grouped by object when an
 * operation changed more than one.
 */
const props = defineProps<{ changes: S['AuditChange'][] }>()

const groups = computed(() => {
  const byObject = new Map<string, S['AuditChange'][]>()
  for (const c of props.changes) {
    const key = c.object ?? ''
    byObject.set(key, [...(byObject.get(key) ?? []), c])
  }
  return [...byObject.entries()].map(([object, changes]) => ({ object, changes }))
})
const several = computed(() => groups.value.length > 1)
</script>

<template>
  <table class="changes">
    <thead>
      <tr>
        <th scope="col">Field</th>
        <th scope="col">Before</th>
        <th scope="col">After</th>
      </tr>
    </thead>
    <tbody v-for="g in groups" :key="g.object">
      <tr v-if="several && g.object" class="object">
        <th colspan="3" scope="rowgroup">{{ g.object }}</th>
      </tr>
      <tr v-for="(c, i) in g.changes" :key="i">
        <template v-if="c.kind === 'created' || c.kind === 'deleted'">
          <td colspan="3" class="muted">{{ c.object ?? c.field }} {{ c.kind }}</td>
        </template>
        <template v-else>
          <td class="field">{{ c.field }}</td>
          <td v-if="c.kind === 'secret'" colspan="2" class="muted">changed (secrets are never logged)</td>
          <td v-else-if="c.kind === 'text'" colspan="2">{{ c.summary }}</td>
          <template v-else>
            <td class="value" :class="{ none: c.old == null }">{{ c.old ?? '–' }}</td>
            <td class="value" :class="{ none: c.new == null }">{{ c.new ?? '–' }}</td>
          </template>
        </template>
      </tr>
    </tbody>
  </table>
</template>

<style scoped>
.changes {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.9em;
}

th,
td {
  text-align: left;
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
  vertical-align: top;
}

thead th {
  font-weight: 600;
  color: var(--pnmt-muted);
}

.object th {
  font-weight: 600;
  padding-top: var(--pnmt-space-2);
}

.field {
  white-space: nowrap;
  font-weight: 500;
}

.value {
  word-break: break-word;
  white-space: pre-wrap;
}

.none {
  color: var(--pnmt-muted);
}
</style>
