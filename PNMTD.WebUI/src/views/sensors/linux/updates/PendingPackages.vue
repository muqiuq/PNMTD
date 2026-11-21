<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import IconField from 'primevue/iconfield'
import InputIcon from 'primevue/inputicon'
import InputText from 'primevue/inputtext'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import type { S } from '@/api/resources'
import { useCopy } from '@/composables/useCopy'
import PackageChangelog from './PackageChangelog.vue'
import { coveredByTrackers, packagesText, ratingLabel, ratingSeverity } from './osUpdatesFormat'

/**
 * A server's pending packages (docs/linux-os-updates-plan.md §10.1 4): installed → candidate, the origin, the security mark, the
 * rating and the CVEs, and the marks only with full upgrade, phased and not covered by the trackers. Filters for all, security
 * and high risk; a row opens its findings and its changelog part; *Copy* gives the list as text.
 */
const props = defineProps<{ view: S['OsUpdatesView'] }>()
const copy = useCopy()

type Show = 'all' | 'security' | 'high'
const show = ref<Show>('all')
const search = ref('')
const shows = (p: S['OsPendingPackageView'], what: Show) =>
  what === 'security' ? p.security : what === 'high' ? p.rating === 'HighRisk' : true
const options = computed(() => {
  const filters: [string, Show][] = [
    ['All', 'all'],
    ['Security', 'security'],
  ]
  if (props.view.checkEnabled) filters.push(['High risk', 'high'])
  return filters.map(([label, value]) => ({
    label: `${label} (${props.view.packages.filter((p) => shows(p, value)).length})`,
    value,
  }))
})
const query = computed(() => search.value.trim().toLowerCase())
const rows = computed(() =>
  props.view.packages
    .filter((p) => shows(p, show.value))
    .filter(
      (p) =>
        !query.value ||
        [p.name, p.source, p.candidate, p.origin, ...p.cves].some((v) => v?.toLowerCase().includes(query.value)),
    )
    .map((p) => ({ ...p, key: `${p.name}:${p.arch}` })),
)
const expanded = ref<Record<string, boolean>>({})
function toggle(key: string) {
  const next = { ...expanded.value }
  if (next[key]) delete next[key]
  else next[key] = true
  expanded.value = next
}
const findingsOf = (p: S['OsPendingPackageView']) => props.view.findings.filter((f) => f.sourcePackage === p.source)
const notCovered = (p: S['OsPendingPackageView']) => props.view.checkEnabled && !coveredByTrackers(p.origin)
</script>

<template>
  <section class="panel" aria-labelledby="pending-title">
    <div class="panel-header">
      <span id="pending-title">Pending packages</span>
      <span class="spacer" />
      <Button
        label="Copy"
        icon="pi pi-copy"
        size="small"
        text
        :disabled="!rows.length"
        @click="copy(packagesText(rows), 'The pending packages')"
      />
    </div>
    <div class="panel-body toolbar">
      <SelectButton
        v-model="show"
        :options="options"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
      />
      <IconField>
        <InputIcon class="pi pi-search" />
        <InputText v-model="search" size="small" placeholder="Package, origin or CVE" aria-label="Search packages" />
      </IconField>
    </div>
    <DataTable
      v-model:expanded-rows="expanded"
      :value="rows"
      data-key="key"
      size="small"
      :paginator="rows.length > 50"
      :rows="50"
      class="packages"
    >
      <template #empty>
        <p v-if="!view.packages.length" class="empty muted">
          {{
            view.pending
              ? 'Not reported yet: the agent sends its list with its next collection.'
              : 'No updates pending.'
          }}
        </p>
        <p v-else-if="query" class="empty muted">No package matches “{{ search.trim() }}”.</p>
        <p v-else class="empty muted">None.</p>
      </template>
      <Column header="Package">
        <template #body="{ data: p }">
          <button type="button" class="toggle" :aria-expanded="!!expanded[p.key]" @click="toggle(p.key)">
            <i :class="expanded[p.key] ? 'pi pi-angle-down' : 'pi pi-angle-right'" aria-hidden="true" />
            <strong>{{ p.name }}</strong>
          </button>
          <span class="muted small"> {{ p.arch }}</span>
          <span v-if="p.source !== p.name" class="muted small"> · source {{ p.source }}</span>
          <div v-if="!p.inUpgrade || p.phased || notCovered(p)" class="marks">
            <Tag v-if="!p.inUpgrade" value="only with full upgrade" severity="secondary" />
            <Tag v-if="p.phased" value="phased" severity="secondary" />
            <Tag v-if="notCovered(p)" value="not covered by the trackers" severity="secondary" />
          </div>
        </template>
      </Column>
      <Column header="Installed → candidate">
        <template #body="{ data: p }">
          <span class="mono small">{{ p.installed ?? 'new' }} → {{ p.candidate }}</span>
        </template>
      </Column>
      <Column header="Origin" style="width: 14rem">
        <template #body="{ data: p }">
          <span class="small muted origin" :title="p.origin ?? undefined">{{ p.origin ?? '–' }}</span>
        </template>
      </Column>
      <Column header="Security" style="width: 6.5rem">
        <template #body="{ data: p }">
          <Tag v-if="p.security" value="security" severity="info" icon="pi pi-shield" />
        </template>
      </Column>
      <Column v-if="view.checkEnabled" header="Rating" style="width: 7rem">
        <template #body="{ data: p }">
          <Tag v-if="p.rating" :value="ratingLabel(p.rating)" :severity="ratingSeverity(p.rating)" />
        </template>
      </Column>
      <Column v-if="view.checkEnabled" header="CVEs" style="width: 11rem">
        <template #body="{ data: p }">
          <span class="mono small">{{ p.cves.slice(0, 3).join(', ') }}</span>
          <span v-if="p.cves.length > 3" class="muted small"> +{{ p.cves.length - 3 }}</span>
        </template>
      </Column>
      <template #expansion="{ data: p }">
        <PackageChangelog
          :sensor-id="view.sensorId"
          :pkg="p"
          :findings="findingsOf(p)"
          :changelogs-allowed="view.rights?.changelogs !== false"
        />
      </template>
    </DataTable>
  </section>
</template>

<style scoped>
.spacer {
  flex: 1;
}

.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding-bottom: var(--pnmt-space-2);
}

.toggle {
  all: unset;
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.toggle:focus-visible {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 2px;
}

.toggle i {
  font-size: 0.8rem;
  color: var(--pnmt-muted);
}

.marks {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
  margin-top: 2px;
}

.marks :deep(.p-tag) {
  font-size: 0.75rem;
}

.origin {
  display: block;
  overflow-wrap: anywhere;
}

.empty {
  margin: 0;
  padding: var(--pnmt-space-3) 0;
}

.small {
  font-size: 0.857rem;
}
</style>
