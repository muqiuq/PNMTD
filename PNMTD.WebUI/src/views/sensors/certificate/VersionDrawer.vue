<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Drawer from 'primevue/drawer'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNow } from '@/composables/useNow'
import CertificateChain from './CertificateChain.vue'
import CertificateFacts from './CertificateFacts.vue'
import CompareTable from './CompareTable.vue'
import { compareFacts, type Trust } from './certificate'

/**
 * A certificate the sensor has seen (docs/ssl-certificate-plan.md §8.3): its facts and chain, its PEM, and *Compare with
 * current*, the two side by side as §8.4's comparison.
 */
const props = defineProps<{
  sensorId: string
  versionId: string | null
  current: S['CertificateItem'] | null
  trust: Trust
}>()
const emit = defineEmits<{ close: [] }>()
const visible = computed({ get: () => props.versionId !== null, set: (v) => !v && emit('close') })
const now = useNow()
const version = useQuery({
  queryKey: computed(() => [...keys.certificate(props.sensorId), 'version', props.versionId]),
  queryFn: () => api.certificates.version(props.sensorId, props.versionId!),
  enabled: computed(() => props.versionId !== null),
})
const comparing = ref(false)
watch(
  () => props.versionId,
  () => (comparing.value = false),
)
const item = computed(() => version.data.value ?? null)
const isCurrent = computed(() => !!item.value && item.value.id === props.current?.id)
const fields = computed(() => (item.value && props.current ? compareFacts(item.value.facts, props.current.facts) : []))
</script>

<template>
  <Drawer
    v-model:visible="visible"
    position="right"
    header="A certificate seen"
    :style="{ width: 'min(48rem, 100vw)' }"
  >
    <ErrorState
      v-if="version.isError.value && !item"
      :error="version.error.value"
      what="the certificate"
      @retry="version.refetch()"
    />
    <div v-else-if="!item" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <div v-else class="content">
      <p class="muted small line">
        First seen <RelativeTime :value="item.firstSeenAt" /> · last seen <RelativeTime :value="item.lastSeenAt" />
        <template v-if="isCurrent"> · the current certificate</template>
      </p>
      <div class="actions">
        <Button
          v-if="current && !isCurrent"
          :label="comparing ? 'Hide the comparison' : 'Compare with current'"
          icon="pi pi-arrow-right-arrow-left"
          size="small"
          outlined
          @click="comparing = !comparing"
        />
        <Button
          as="a"
          :href="api.certificates.pemUrl(sensorId, item.id)"
          label="Certificate (PEM)"
          icon="pi pi-download"
          size="small"
          text
        />
        <Button
          as="a"
          :href="api.certificates.pemUrl(sensorId, item.id, true)"
          label="Chain (PEM)"
          icon="pi pi-download"
          size="small"
          text
        />
      </div>
      <CompareTable
        v-if="comparing && fields.length"
        :fields="fields"
        left="This one"
        right="Current"
        label="Compared with the current certificate"
      />
      <CertificateFacts :facts="item.facts" :now="now" />
      <CertificateChain :chain="item.chain" :trust="trust" :self-signed="item.facts.selfSigned" />
    </div>
  </Drawer>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}

.content {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.line {
  margin: 0;
}

.actions {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.small {
  font-size: 0.857rem;
}
</style>
