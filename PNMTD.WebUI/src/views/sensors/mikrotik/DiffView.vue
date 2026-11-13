<script setup lang="ts">
import type { S } from '@/api/resources'

/**
 * A diff between two exports (docs/mikrotik-plan.md §6.4): each hunk with its header, and every line
 * with its number in the old and the new version, added lines green, removed lines red.
 */
defineProps<{ diff: S['MikrotikBackupDiff'] }>()

const mark = (kind: S['DiffLineKind']) => (kind === 'Added' ? '+' : kind === 'Removed' ? '−' : ' ')
</script>

<template>
  <div class="diff" role="table" aria-label="Changes">
    <p v-if="!diff.hunks.length" class="muted same">The two versions are the same.</p>
    <template v-for="(hunk, h) in diff.hunks" :key="h">
      <div class="hunk" role="row">
        <span role="cell">@@ −{{ hunk.oldStart }},{{ hunk.oldCount }} +{{ hunk.newStart }},{{ hunk.newCount }} @@</span>
      </div>
      <div v-for="(line, i) in hunk.lines" :key="`${h}-${i}`" class="line" :class="line.kind.toLowerCase()" role="row">
        <span class="number" role="cell">{{ line.oldNumber ?? '' }}</span>
        <span class="number" role="cell">{{ line.newNumber ?? '' }}</span>
        <span class="mark" role="cell" aria-hidden="true">{{ mark(line.kind) }}</span>
        <span class="text" role="cell"
          ><span class="sr-only">{{ line.kind === 'Context' ? '' : line.kind + ': ' }}</span
          >{{ line.text }}</span
        >
      </div>
    </template>
  </div>
</template>

<style scoped>
.diff {
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-bg);
  overflow: auto;
  max-height: 32rem;
}

.same {
  margin: 0;
  padding: var(--pnmt-space-2);
}

.hunk {
  padding: 2px var(--pnmt-space-2);
  color: var(--pnmt-muted);
  background: var(--pnmt-none-soft);
}

.line {
  display: grid;
  grid-template-columns: 3.5rem 3.5rem 1.5rem 1fr;
  white-space: pre;
}

.number {
  text-align: right;
  padding-right: var(--pnmt-space-2);
  color: var(--pnmt-muted);
  user-select: none;
}

.mark {
  text-align: center;
  user-select: none;
}

.added {
  background: var(--pnmt-ok-soft);
}

.removed {
  background: var(--pnmt-fail-soft);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}
</style>
