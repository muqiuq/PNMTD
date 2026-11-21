<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys } from '@/api/resources'
import { useSession } from '@/stores/session'
import { listsFooter, summaryChips } from '@/views/sensors/linux/updates/osUpdatesFormat'
import OsUpdatesRows from './OsUpdatesRows.vue'

/**
 * The dashboard's *OS updates* card (docs/linux-os-updates-plan.md §10.3), for Monitoring: View while OS updates are on and at
 * least one Linux server has an agent: the fleet's pending updates as chips (servers with updates, packages, security, high risk
 * with the acknowledged ones apart, failed runs, stale lists), by server or by package, at most 8 rows and *Show all* in a dialog.
 * From its own endpoint, every 30 s as the dashboard; the dashboard's cache doesn't change. Collapsible on phones, as *Recent
 * changes* is.
 */
const props = defineProps<{ collapsible?: boolean }>()
const session = useSession()
const summary = useQuery({
  queryKey: [...keys.osUpdates, 'summary'],
  queryFn: api.linux.osUpdates.summary,
  refetchInterval: 30_000,
  refetchOnWindowFocus: true,
  enabled: computed(() => session.can('Monitoring', 'View')),
})
const s = computed(() =>
  summary.data.value?.enabled && summary.data.value.servers > 0 ? summary.data.value : undefined,
)
const open = ref(!props.collapsible)
type By = 'server' | 'package'
const by = ref<By>('server')
const views = [
  { label: 'By server', value: 'server' },
  { label: 'By package', value: 'package' },
]
const MAX = 8
const rows = computed(() =>
  by.value === 'server' ? (s.value?.byServer.length ?? 0) : (s.value?.byPackage.length ?? 0),
)
const showingAll = ref(false)
</script>

<template>
  <section v-if="s" class="panel os-updates-card" aria-labelledby="os-updates-card-title">
    <header class="panel-header">
      <button v-if="collapsible" type="button" class="toggle" :aria-expanded="open" @click="open = !open">
        <i :class="open ? 'pi pi-angle-down' : 'pi pi-angle-right'" aria-hidden="true" />
        <span id="os-updates-card-title">OS updates</span>
      </button>
      <span v-else id="os-updates-card-title">OS updates</span>
      <span class="chips">
        <Tag v-for="c in summaryChips(s)" :key="c.label" :value="c.label" :severity="c.severity" />
      </span>
      <SelectButton
        v-if="open && (s.byServer.length || s.byPackage.length)"
        v-model="by"
        :options="views"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        class="by"
        aria-label="Show the OS updates"
      />
    </header>
    <template v-if="open">
      <p v-if="!rows" class="empty muted small">No updates pending on any server.</p>
      <OsUpdatesRows v-else-if="by === 'server'" :servers="s.byServer.slice(0, MAX)" />
      <OsUpdatesRows v-else :packages="s.byPackage.slice(0, MAX)" />
      <footer class="footer small">
        <span :class="s.listsStale ? 'warn' : 'muted'">{{ listsFooter(s) }}</span>
        <Button
          v-if="rows > MAX"
          :label="`Show all ${rows}`"
          size="small"
          link
          class="all"
          @click="showingAll = true"
        />
      </footer>
    </template>
    <Dialog
      v-model:visible="showingAll"
      header="OS updates"
      modal
      dismissable-mask
      :style="{ width: '56rem' }"
      :breakpoints="{ '960px': '95vw' }"
    >
      <SelectButton
        v-model="by"
        :options="views"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Show the OS updates"
      />
      <OsUpdatesRows v-if="by === 'server'" :servers="s.byServer" class="dialog-rows" />
      <OsUpdatesRows v-else :packages="s.byPackage" class="dialog-rows" />
    </Dialog>
  </section>
</template>

<style scoped>
.panel-header {
  flex-wrap: wrap;
  padding-top: var(--pnmt-space-1);
  padding-bottom: var(--pnmt-space-1);
}

.toggle {
  all: unset;
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  cursor: pointer;
}

.chips {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
  font-weight: 400;
}

.chips :deep(.p-tag) {
  font-size: 0.75rem;
  padding: 0 0.4rem;
}

.by {
  margin-left: auto;
}

.empty {
  margin: 0;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
}

.footer {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: 0 var(--pnmt-space-3) var(--pnmt-space-2);
}

.all {
  margin-left: auto;
  padding: 0;
}

.warn {
  color: var(--pnmt-warn);
}

.dialog-rows {
  margin-top: var(--pnmt-space-2);
}

.small {
  font-size: 0.857rem;
}
</style>
