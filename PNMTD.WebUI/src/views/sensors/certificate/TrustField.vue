<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useSession } from '@/stores/session'
import AddAuthorityDialog from '@/views/admin/certificates/AddAuthorityDialog.vue'
import { TRUST_OPTIONS, type Trust } from './certificate'

/**
 * The trust of an SSL certificate sensor (docs/ssl-certificate-plan.md §8.2): public CAs, a private CA, or pinned; in the
 * private mode a multi-select of PNMT's private CAs, with *Add a CA…* for Certificates: Manage, which opens §8.5's dialog
 * in place and picks the new CA.
 */
const trust = defineModel<Trust>('trust', { required: true })
const authorityIds = defineModel<string[]>('authorityIds', { required: true })
defineProps<{ inputId?: string; invalidTrust?: boolean; invalidAuthorities?: boolean }>()
const session = useSession()
const canManage = computed(() => session.can('Certificates', 'Manage'))
const authorities = useQuery({
  queryKey: keys.certificateAuthorities,
  queryFn: api.certificateAuthorities.list,
  enabled: computed(() => trust.value === 'Private'),
})
const adding = ref(false)
function added(item: S['CertificateAuthorityItem']) {
  authorityIds.value = [...authorityIds.value.filter((id) => id !== item.id), item.id]
}
</script>

<template>
  <div class="trust-field">
    <Select
      v-model="trust"
      :input-id="inputId"
      :options="TRUST_OPTIONS"
      option-label="label"
      option-value="value"
      :invalid="invalidTrust"
      aria-label="Trust"
    />
    <template v-if="trust === 'Private'">
      <MultiSelect
        v-model="authorityIds"
        :options="authorities.data.value ?? []"
        option-label="name"
        option-value="id"
        display="chip"
        filter
        placeholder="Choose private CAs"
        :loading="authorities.isLoading.value"
        :invalid="invalidAuthorities"
        aria-label="Private CAs"
        class="cas"
      />
      <Button v-if="canManage" label="Add a CA…" icon="pi pi-plus" size="small" text @click="adding = true" />
      <small v-else-if="authorities.data.value && !authorities.data.value.length" class="field-hint"
        >No private CA yet: Certificates: Manage adds them on Admin → Certificates.</small
      >
    </template>
    <AddAuthorityDialog v-if="canManage" v-model:visible="adding" @added="added" />
  </div>
</template>

<style scoped>
.trust-field {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
  align-items: center;
}

.cas {
  min-width: 14rem;
  flex: 1;
}
</style>
