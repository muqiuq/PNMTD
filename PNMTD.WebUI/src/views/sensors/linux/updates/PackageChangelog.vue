<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import FindingReasons from './FindingReasons.vue'
import { ratingLabel, ratingSeverity } from './osUpdatesFormat'

/**
 * A pending package opened (docs/linux-os-updates-plan.md §10.1 4, §6.2): the findings of its source package with their reasons,
 * and its changelog's part between the installed and the candidate version as a server fetched it. A changelog comes from the
 * package's repository: untrusted, shown as text only.
 */
const props = defineProps<{
  sensorId: string
  pkg: S['OsPendingPackageView']
  findings: S['OsFindingView'][]
  changelogsAllowed: boolean
}>()
const changelog = useQuery({
  queryKey: computed(() => [
    ...keys.osUpdatesServer(props.sensorId),
    'changelog',
    props.pkg.source,
    props.pkg.sourceVersion,
  ]),
  queryFn: async () => {
    try {
      return await api.linux.osUpdates.changelog(props.sensorId, props.pkg.name)
    } catch (e) {
      if ((e as { status?: number }).status === 404) return null
      throw e
    }
  },
  enabled: computed(() => props.pkg.hasChangelog),
  staleTime: 5 * 60_000,
})
const c = computed(() => changelog.data.value ?? null)
</script>

<template>
  <div class="package-changelog">
    <div v-if="findings.length" class="findings">
      <div v-for="f in findings" :key="f.id" class="finding">
        <div class="line">
          <span class="mono cve">{{ f.cve }}</span>
          <Tag :value="ratingLabel(f.rating)" :severity="ratingSeverity(f.rating)" />
          <Tag v-if="f.acknowledgement" value="acknowledged" severity="secondary" />
        </div>
        <FindingReasons :reasons="f.reasons" />
      </div>
    </div>
    <p v-if="!pkg.hasChangelog" class="small muted none">
      <template v-if="!changelogsAllowed"
        >No changelog: <code>os_updates.changelogs: false</code> in config.yaml on the server.</template
      >
      <template v-else
        >Changelog not available: the server sent none for {{ pkg.source }} {{ pkg.sourceVersion }}.</template
      >
    </p>
    <p v-else-if="changelog.isPending.value" class="small muted none">Loading the changelog…</p>
    <p v-else-if="changelog.isError.value" class="small bad none">The changelog could not be read.</p>
    <template v-else-if="c">
      <div class="small muted">
        Changelog of {{ c.source }} <span class="mono">{{ c.version }}</span><template v-if="c.installedVersion">
          since <span class="mono">{{ c.installedVersion }}</span></template
        ><template v-if="c.urgencies.length"> · urgency {{ c.urgencies.join(', ') }}</template
        ><template v-if="c.cves.length"> · {{ c.cves.length }} CVEs named</template> · received
        <RelativeTime :value="c.receivedAt" /><template v-if="c.fetchedFrom"> from {{ c.fetchedFrom }}</template>
      </div>
      <pre class="changelog">{{ c.text }}</pre>
    </template>
    <p v-else class="small muted none">Changelog not available.</p>
  </div>
</template>

<style scoped>
.package-changelog {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-1) 0;
  min-width: 0;
}

.findings {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.cve {
  font-weight: 600;
}

.changelog {
  margin: 0;
  padding: var(--pnmt-space-2);
  max-height: 20rem;
  overflow: auto;
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
}

.none {
  margin: 0;
}

.small {
  font-size: 0.857rem;
}

.bad {
  color: var(--pnmt-fail);
}
</style>
