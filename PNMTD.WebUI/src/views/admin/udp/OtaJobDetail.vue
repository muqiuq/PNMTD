<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { computed } from 'vue'
import { api, keys, type S } from '@/api/resources'
import OtaJobCard from '@/views/sensors/udp/OtaJobCard.vue'

/** An update opened on the admin page: with its log from the node's updates, or alone once the node is gone. */
const props = defineProps<{ job: S['UdpOtaJobItem'] }>()
const emit = defineEmits<{ cancel: [job: S['UdpOtaJobItem']] }>()

const jobs = useQuery({
  queryKey: computed(() => [...keys.udpNode(props.job.sensorId ?? 'none'), 'ota-jobs']),
  queryFn: () => api.udp.nodeJobs(props.job.sensorId!),
  enabled: computed(() => !!props.job.sensorId),
})
const log = computed(() => jobs.data.value?.find((j) => j.job.id === props.job.id)?.log ?? null)
</script>

<template>
  <OtaJobCard :job="job" :log="log" can-cancel @cancel="(j) => emit('cancel', j)" />
</template>
