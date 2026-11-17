<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import { computed, ref } from 'vue'
import type { S } from '@/api/resources'
import { formatDay } from '@/format'
import AcceptChangeDialog from './AcceptChangeDialog.vue'

/**
 * A change waiting for a person (docs/ssl-certificate-plan.md §8.3), on the Overview and the Certificate tab, for everyone
 * who sees the sensor: what changed and since when, and *Review and accept…* for Certificates: Edit, which opens §8.4's
 * window.
 */
const props = defineProps<{ sensor: S['SensorDetail']; canAccept: boolean }>()
const pending = computed(() => props.sensor.certificate?.changePending ?? null)
const reviewing = ref(false)
</script>

<template>
  <Message v-if="pending" severity="warn" icon="pi pi-exclamation-triangle" :closable="false" class="change-banner">
    <div class="content">
      <span
        >The certificate changed unexpectedly on {{ formatDay(new Date(pending.since)) }}:
        {{ pending.kinds.join(', ') }}.</span
      >
      <Button
        v-if="canAccept"
        label="Review and accept…"
        severity="warn"
        size="small"
        outlined
        @click="reviewing = true"
      />
    </div>
  </Message>
  <AcceptChangeDialog v-if="canAccept && pending" v-model:visible="reviewing" :sensor="sensor" />
</template>

<style scoped>
.content {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-3);
}
</style>
