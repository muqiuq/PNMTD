<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'

/** A run's routers (§8.7): state, step, error, what was read, how long. */
const props = defineProps<{ scanId: string; live: boolean }>()
const detail = useQuery({
  queryKey: computed(() => [...keys.network, 'scan', props.scanId]),
  queryFn: () => api.network.scan(props.scanId),
  refetchInterval: computed(() => (props.live ? 3000 : false)),
})
const severity = (s: string | null) =>
  s === 'Succeeded' ? 'success' : s === 'Failed' ? 'danger' : s === 'Running' ? 'info' : 'secondary'
</script>

<template>
  <table v-if="detail.data.value" class="items">
    <tbody>
      <tr v-for="r in detail.data.value.routers" :key="r.id">
        <td>
          <RouterLink v-if="r.sensorId" :to="{ name: 'network-map', query: { focus: `router:${r.sensorId}` } }">{{
            r.routerName
          }}</RouterLink>
          <span v-else>{{ r.routerName }}</span>
        </td>
        <td><Tag :value="r.state" :severity="severity(r.state)" /></td>
        <td class="small">
          <template v-if="r.state === 'Running'">{{ r.step }}</template>
          <template v-else-if="r.error">{{ r.error }}</template>
          <template v-else-if="r.counts">{{
            Object.entries(r.counts)
              .filter(([, v]) => v)
              .map(([k, v]) => `${v} ${k}`)
              .join(', ')
          }}</template>
          <div v-for="(error, table) in r.tableErrors ?? {}" :key="table" class="warn">{{ table }}: {{ error }}</div>
        </td>
        <td class="small muted">{{ r.durationMs != null ? `${(r.durationMs / 1000).toFixed(1)} s` : '' }}</td>
      </tr>
    </tbody>
  </table>
</template>

<style scoped>
.items {
  width: 100%;
  border-collapse: collapse;
}

.items td {
  padding: 0.25rem 0.5rem 0.25rem 0;
  border-bottom: 1px solid var(--pnmt-border);
  vertical-align: top;
}

.small {
  font-size: 0.857rem;
}

.warn {
  color: var(--pnmt-warn);
}
</style>
