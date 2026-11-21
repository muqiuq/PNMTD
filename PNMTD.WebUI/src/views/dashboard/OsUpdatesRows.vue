<script setup lang="ts">
import Tag from 'primevue/tag'
import type { S } from '@/api/resources'
import { formatDay } from '@/format'
import { ratingLabel, ratingSeverity } from '@/views/sensors/linux/updates/osUpdatesFormat'

/**
 * The rows of the dashboard's *OS updates* card (docs/linux-os-updates-plan.md §10.3), by server or by package: a server links
 * to its OS updates tab, with its counts and badges; a package names its candidate, on how many servers (their names on hover),
 * whether it is a security update, its rating and CVEs.
 */
defineProps<{ servers?: S['OsUpdatesServerRow'][]; packages?: S['OsUpdatesPackageRow'][] }>()
const tab = (r: S['OsUpdatesServerRow']) => ({ name: 'sensor', params: { id: r.sensorId, tab: 'os-updates' } })
</script>

<template>
  <ul v-if="servers" class="os-rows">
    <li v-for="r in servers" :key="r.sensorId" class="row">
      <RouterLink :to="tab(r)" class="name ellipsis" :title="`${r.hostName} / ${r.name}`">{{ r.hostName }}</RouterLink>
      <span class="counts small"
        >{{ r.pending }} pending<template v-if="r.security"> · {{ r.security }} security</template></span
      >
      <span class="badges">
        <Tag v-if="r.highRisk" :value="`${r.highRisk} high risk`" severity="danger" />
        <Tag
          v-if="r.highRiskAcknowledged"
          :value="r.acknowledgedUntil ? `acknowledged until ${formatDay(r.acknowledgedUntil)}` : 'acknowledged'"
          severity="secondary"
        />
        <Tag v-if="r.failed" value="last run failed" severity="danger" :title="r.failedMessage ?? undefined" />
        <Tag v-if="r.running" value="running now" severity="info" />
        <Tag v-if="r.rebootRequired" value="reboot pending" severity="warn" />
        <Tag v-if="r.listsStale" value="lists older than 2 days" severity="warn" />
      </span>
    </li>
  </ul>
  <ul v-else-if="packages" class="os-rows">
    <li v-for="p in packages" :key="`${p.source} ${p.version}`" class="row">
      <span class="name ellipsis"
        ><strong>{{ p.source }}</strong> <span class="mono small">{{ p.version }}</span></span
      >
      <span class="counts small" :title="p.servers.join(', ')">{{
        p.serverCount === 1 ? '1 server' : `${p.serverCount} servers`
      }}</span>
      <span class="badges">
        <Tag v-if="p.rating" :value="ratingLabel(p.rating)" :severity="ratingSeverity(p.rating)" />
        <Tag v-if="p.security" value="security" severity="info" />
        <span v-if="p.cves.length" class="mono small muted ellipsis" :title="p.cves.join(', ')"
          >{{ p.cves.slice(0, 2).join(', ')
          }}<template v-if="p.cves.length > 2"> +{{ p.cves.length - 2 }}</template></span
        >
      </span>
    </li>
  </ul>
</template>

<style scoped>
.os-rows {
  list-style: none;
  margin: 0;
  padding: var(--pnmt-space-1) 0;
}

.row {
  display: grid;
  grid-template-columns: minmax(0, 12rem) max-content minmax(0, 1fr);
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: 2px var(--pnmt-space-3);
  min-height: 26px;
}

.badges {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1);
  min-width: 0;
}

.badges :deep(.p-tag) {
  font-size: 0.75rem;
  padding: 0 0.4rem;
}

.counts {
  white-space: nowrap;
}

.small {
  font-size: 0.857rem;
}

@media (max-width: 599px) {
  .row {
    grid-template-columns: minmax(0, 1fr) max-content;
  }

  .badges {
    grid-column: 1 / -1;
  }
}
</style>
