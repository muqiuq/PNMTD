<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import ProgressSpinner from 'primevue/progressspinner'
import { computed } from 'vue'
import { api, keys, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import { useActions } from '@/composables/useActions'
import { isOpenUpdate } from '@/format/udp'
import OtaJobCard from './OtaJobCard.vue'

/**
 * A node's firmware (docs/udp-iot-plan.md §6.2): the version it runs (from BOOT's INFO or OTA
 * APPLIED), and its updates with progress and their command-log timeline. Only an Admin starts or
 * cancels an update; every role sees them.
 */
const props = defineProps<{ sensor: S['SensorDetail']; isAdmin: boolean }>()
const emit = defineEmits<{ update: [] }>()
const { ask, done } = useActions()

const jobs = useQuery({
  queryKey: computed(() => [...keys.udpNode(props.sensor.id), 'ota-jobs']),
  queryFn: () => api.udp.nodeJobs(props.sensor.id),
  // A running download moves every second; finished ones do not.
  refetchInterval: (q) => (q.state.data?.some((j) => isOpenUpdate(j.job.state)) ? 5_000 : 30_000),
})
const hasOpen = computed(() => (jobs.data.value ?? []).some((j) => isOpenUpdate(j.job.state)))

async function cancel(job: S['UdpOtaJobItem']) {
  const ok = await ask(
    `Cancel the update to ${job.firmwareVersion}?`,
    'An offer not yet answered is not sent again; a running download gets the abort answer with its next GET, and the device discards the partial image.',
    'Cancel update',
  )
  if (ok) {
    await done(() => api.udp.cancelUpdate(job.id), {
      success: 'Update cancelled',
      invalidate: [keys.udp, keys.sensor(props.sensor.id)],
    })
  }
}
</script>

<template>
  <div class="tab">
    <section class="panel running">
      <div class="panel-body line">
        <span class="muted">Running</span>
        <span class="mono version">{{ sensor.udp?.firmwareVersion ?? 'unknown' }}</span>
        <span v-if="!sensor.udp?.firmwareVersion" class="muted small"
          >A BOOT packet with the version as INFO, or an applied update, sets it.</span
        >
        <Button
          v-if="isAdmin"
          label="Update firmware…"
          icon="pi pi-upload"
          size="small"
          class="end"
          :disabled="hasOpen"
          :title="hasOpen ? 'An update is open already' : undefined"
          @click="emit('update')"
        />
      </div>
    </section>
    <ErrorState
      v-if="jobs.isError.value && !jobs.data.value"
      :error="jobs.error.value"
      what="the updates"
      @retry="jobs.refetch()"
    />
    <div v-else-if="!jobs.data.value" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <EmptyState
      v-else-if="!jobs.data.value.length"
      class="panel"
      icon="pi pi-upload"
      title="No firmware update yet."
      :text="
        isAdmin
          ? 'Upload an image on Admin › UDP nodes › Firmware, then update this node.'
          : 'An Admin uploads images and starts updates.'
      "
    />
    <template v-else>
      <OtaJobCard
        v-for="j in jobs.data.value"
        :key="j.job.id"
        :job="j.job"
        :log="j.log"
        :can-cancel="isAdmin"
        @cancel="cancel"
      />
    </template>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
  flex-wrap: wrap;
}

.version {
  font-weight: 600;
  font-size: 1.071rem;
}

.small {
  font-size: 0.857rem;
}

.end {
  margin-left: auto;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}
</style>
