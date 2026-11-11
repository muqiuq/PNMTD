<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { api, keys } from '@/api/resources'
import PageHeader from '@/components/PageHeader.vue'
import { useActions } from '@/composables/useActions'

/**
 * A DNS zone, new or edited (§5.6): the zone file in BIND format is the reference; PNMTD resolves its
 * records and reports where the resolvers answer differently.
 */
const route = useRoute()
const router = useRouter()
const { invalidate, notify } = useActions()

const id = computed(() => (route.params.id ? String(route.params.id) : null))
const existing = useQuery({
  queryKey: computed(() => [...keys.dns, id.value]),
  queryFn: () => api.dns.get(id.value!),
  enabled: computed(() => !!id.value),
})
const hosts = useQuery({
  queryKey: [...keys.hosts, 'all'],
  queryFn: () => api.hosts.list({ pageSize: 500, sort: 'name' }),
})

const zoneFile = ref(
  `$ORIGIN example.org.\n$TTL 3600\n@        IN NS    ns1.example.org.\n@        IN A     203.0.113.10\nwww      IN A     203.0.113.11\n`,
)
const hostId = ref<string | null>(null)
const interval = ref<number | null>(3600)
const enabled = ref(true)
const errors = ref<Record<string, string | undefined>>({})
const busy = ref(false)

watch(
  () => existing.data.value,
  (zone) => {
    if (zone) {
      zoneFile.value = zone.zoneFileContent
      hostId.value = zone.host?.id ?? null
      interval.value = zone.interval
      enabled.value = zone.enabled
    }
  },
  { immediate: true },
)

async function submit() {
  errors.value = {
    zoneFileContent: zoneFile.value.trim() ? undefined : 'Paste the zone file.',
    interval: interval.value && interval.value > 0 ? undefined : 'In seconds, more than 0.',
  }
  if (Object.values(errors.value).some((e) => e)) return
  busy.value = true
  const body = {
    zoneFileContent: zoneFile.value,
    hostId: hostId.value,
    interval: interval.value!,
    enabled: enabled.value,
  }
  try {
    const zone = id.value ? await api.dns.update(id.value, body) : await api.dns.create(body)
    await invalidate([keys.dns])
    notify.success(id.value ? `${zone.zoneName} saved` : `${zone.zoneName} created`, 'Its records are checked shortly.')
    await router.replace({ name: 'dns-zone', params: { id: zone.id } })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = {
        zoneFileContent: e.field('zoneFileContent'),
        interval: e.field('interval'),
        hostId: e.field('hostId'),
      }
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="page">
    <PageHeader
      :title="id ? `Edit ${existing.data.value?.zoneName ?? ''}` : 'New DNS zone'"
      :crumbs="[
        { label: 'DNS zones', to: { name: 'dns-zones' } },
        ...(id && existing.data.value
          ? [{ label: existing.data.value.zoneName, to: { name: 'dns-zone', params: { id } } }]
          : []),
      ]"
    />
    <form class="panel panel-body form-grid box" novalidate @submit.prevent="submit">
      <div class="field wide">
        <label for="zone-file">Zone file</label>
        <Textarea
          id="zone-file"
          v-model="zoneFile"
          rows="16"
          class="zone-file"
          spellcheck="false"
          :invalid="!!errors.zoneFileContent"
        />
        <small v-if="errors.zoneFileContent" class="field-error">{{ errors.zoneFileContent }}</small>
        <small v-else class="field-hint"
          >BIND format with $ORIGIN. Every record in it is checked against what the resolvers answer.</small
        >
      </div>
      <div class="field">
        <label for="zone-host">Host</label>
        <Select
          v-model="hostId"
          input-id="zone-host"
          :options="hosts.data.value?.items ?? []"
          option-label="name"
          option-value="id"
          placeholder="No host"
          show-clear
          filter
        />
        <small class="field-hint">The zone's mismatches count as a failure of this host.</small>
      </div>
      <div class="field">
        <label for="zone-interval">Check every (seconds)</label>
        <InputNumber
          v-model="interval"
          input-id="zone-interval"
          :min="60"
          :use-grouping="false"
          :invalid="!!errors.interval"
        />
        <small v-if="errors.interval" class="field-error">{{ errors.interval }}</small>
      </div>
      <div class="switch-row wide">
        <ToggleSwitch v-model="enabled" input-id="zone-enabled" />
        <label for="zone-enabled">Enabled</label>
      </div>
      <div class="form-actions wide">
        <Button type="submit" :label="id ? 'Save zone' : 'Create zone'" :loading="busy" />
        <Button label="Cancel" severity="secondary" text @click="router.back()" />
      </div>
    </form>
  </div>
</template>

<style scoped>
.box {
  max-width: 900px;
}

.zone-file {
  font-family: var(--pnmt-mono);
  font-size: 0.929rem;
  line-height: 1.5;
}

.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
