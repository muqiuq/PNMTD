<script setup lang="ts">
import Tag from 'primevue/tag'
import type { FieldChange } from './certificate'

/**
 * Two certificates side by side (docs/ssl-certificate-plan.md §8.4): the SHA-256, the key, the issuer, the subject, the
 * names (added and removed), the serial and the validity; the rows that differ highlighted, and tagged where `tag` says.
 */
withDefaults(
  defineProps<{
    fields: FieldChange[]
    left?: string
    right?: string
    label: string
    tag?: (field: string) => string | null
  }>(),
  { left: 'Accepted', right: 'New', tag: () => null },
)
const mono = (field: string) => /SHA|Serial/.test(field)
</script>

<template>
  <div class="table-wrap">
    <table class="compare" :aria-label="label">
      <thead>
        <tr>
          <th scope="col"><span class="sr-only">Field</span></th>
          <th scope="col">{{ left }}</th>
          <th scope="col">{{ right }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="f in fields" :key="f.field" :class="{ changed: f.changed }">
          <th scope="row">
            {{ f.field }}
            <Tag v-if="tag(f.field)" :value="tag(f.field)!" severity="warn" class="kind" />
          </th>
          <td :class="{ mono: mono(f.field) }">{{ f.accepted ?? '–' }}</td>
          <td :class="{ mono: mono(f.field) }">{{ f.current ?? '–' }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.table-wrap {
  overflow-x: auto;
}

.compare {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.9286rem;
}

.compare th,
.compare td {
  text-align: left;
  vertical-align: top;
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
  overflow-wrap: anywhere;
}

.compare thead th {
  color: var(--pnmt-muted);
  font-weight: 600;
}

.compare tbody th {
  font-weight: 500;
  width: 11rem;
}

.compare tr.changed td,
.compare tr.changed th {
  background: var(--pnmt-warn-soft);
}

.kind {
  margin-left: var(--pnmt-space-1);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}
</style>
