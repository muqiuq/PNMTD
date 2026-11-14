<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import { useSession } from '@/stores/session'
import TicketChip from './TicketChip.vue'

/**
 * A failing sensor's open ticket (docs/audit-redmine-zabbix-plan.md §6.6): "#1234 In progress, Anna" in its status, the
 * sensor's own or its host's or location's. The dashboard's problems panel asks the same.
 */
const props = defineProps<{ sensorId: string }>()
const session = useSession()
const problems = useQuery({
  queryKey: [...keys.redmine, 'problems'],
  queryFn: api.redmine.problems,
  enabled: computed(() => !!session.info?.redmine && session.can('Redmine', 'View')),
  refetchInterval: 60_000,
})
const ticket = computed(() => problems.data.value?.find((p) => p.sensorId === props.sensorId)?.ticket)
</script>

<template>
  <TicketChip v-if="ticket" :ticket="ticket" />
</template>
