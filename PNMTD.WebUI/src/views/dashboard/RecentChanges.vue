<script setup lang="ts">
import { computed, ref } from 'vue'
import type { S } from '@/api/resources'
import CodeTag from '@/components/CodeTag.vue'
import { useStatusCodes } from '@/composables/useLookups'
import { formatAbsolute, formatDuration } from '@/format'

/**
 * Transitions of the last 24 hours (§5.5), newest first: OK → failed, and failed → OK with how long
 * it was down. They replace v0.12's "last 10 error events", which one flapping sensor could fill.
 */
const props = defineProps<{ changes: S['StateChange'][]; collapsible?: boolean }>()
const codes = useStatusCodes()
const open = ref(!props.collapsible)

const time = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' })
const rows = computed(() => props.changes.slice(0, 15))
</script>

<template>
  <section class="panel recent" aria-labelledby="recent-title">
    <header class="panel-header">
      <button v-if="collapsible" type="button" class="toggle" :aria-expanded="open" @click="open = !open">
        <i :class="open ? 'pi pi-angle-down' : 'pi pi-angle-right'" aria-hidden="true" />
        <span id="recent-title">Recent state changes</span>
      </button>
      <span v-else id="recent-title">Recent state changes <span class="muted">(24 h)</span></span>
      <RouterLink :to="{ name: 'events', query: { range: '24h' } }" class="all"
        >All <i class="pi pi-angle-right"
      /></RouterLink>
    </header>
    <template v-if="open">
      <div v-if="!rows.length" class="empty muted">No changes in the last 24 hours.</div>
      <ul v-else class="changes">
        <li v-for="change in rows" :key="`${change.sensorId}-${change.at}`" class="change">
          <time class="at muted" :datetime="change.at" :title="formatAbsolute(change.at)">{{
            time.format(new Date(change.at))
          }}</time>
          <span class="who ellipsis">
            <RouterLink :to="{ name: 'host', params: { id: change.hostId } }">{{ change.hostName }}</RouterLink>
            <span class="muted"> / </span>
            <RouterLink :to="{ name: 'sensor', params: { id: change.sensorId } }">{{ change.sensorName }}</RouterLink>
          </span>
          <span class="what">
            <template v-if="change.success">
              <CodeTag :code="change.previousCode" compact :name="codes.nameOf(change.previousCode)" />
              <i class="pi pi-arrow-right muted" aria-label="to" />
              <span class="ok">OK</span>
              <span v-if="change.downSeconds" class="muted">
                (down {{ formatDuration(change.downSeconds * 1000) }})</span
              >
            </template>
            <template v-else>
              <span class="ok">OK</span>
              <i class="pi pi-arrow-right muted" aria-label="to" />
              <CodeTag :code="change.code" :name="codes.nameOf(change.code)" />
            </template>
          </span>
        </li>
      </ul>
    </template>
  </section>
</template>

<style scoped>
.toggle {
  all: unset;
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  cursor: pointer;
}

.all {
  margin-left: auto;
  font-weight: 400;
  font-size: 0.929rem;
}

.empty {
  padding: var(--pnmt-space-3);
}

.changes {
  list-style: none;
  margin: 0;
  padding: var(--pnmt-space-1) 0;
}

.change {
  display: grid;
  grid-template-columns: max-content minmax(0, 15rem) minmax(0, 1fr);
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: 2px var(--pnmt-space-3);
  min-height: 26px;
}

.at {
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.what {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
  white-space: nowrap;
}

.what i {
  font-size: 0.75rem;
}

.ok {
  color: var(--pnmt-ok);
  font-weight: 500;
}

@media (max-width: 599px) {
  .change {
    grid-template-columns: max-content minmax(0, 1fr);
  }

  .what {
    grid-column: 2;
  }
}
</style>
