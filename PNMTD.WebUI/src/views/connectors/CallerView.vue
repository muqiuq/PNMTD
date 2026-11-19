<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import ErrorState from '@/components/ErrorState.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import NetworksEditor from './NetworksEditor.vue'
import { typeLabel } from './connectorFormat'

/**
 * A caller (docs/connectors-plan.md §6, §13.4): its key, masked, with *Show* and *Copy* (each display audited) and *Rotate…*; its
 * networks; the connectors granted to it; enabled, which needs a network; its recent calls; and how to call.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const canManage = computed(() => session.can('Connectors', 'Manage'))
const id = computed(() => String(route.params.id))
const caller = useQuery({
  queryKey: computed(() => keys.connectorCaller(id.value)),
  queryFn: () => api.connectors.caller(id.value),
})
const connectors = useQuery({
  queryKey: [...keys.connectors, 'list', 'all'],
  queryFn: () => api.connectors.list({ pageSize: 500 }),
  enabled: canManage,
})
const name = ref('')
const description = ref('')
const networks = ref<S['ConnectorNetworkRequest'][]>([])
const granted = ref<string[]>([])
const enabled = ref(false)
watch(
  () => caller.data.value,
  (c) => {
    if (!c) return
    name.value = c.name
    description.value = c.description ?? ''
    networks.value = c.networks.map((n) => ({ cidr: n.cidr, label: n.label }))
    granted.value = c.connectors.map((x) => x.id)
    enabled.value = c.enabled
  },
  { immediate: true },
)
const dirty = computed(() => {
  const c = caller.data.value
  if (!c) return false
  return (
    name.value !== c.name ||
    description.value !== (c.description ?? '') ||
    enabled.value !== c.enabled ||
    JSON.stringify(networks.value.filter((n) => n.cidr.trim()).map((n) => [n.cidr.trim(), n.label || null])) !==
      JSON.stringify(c.networks.map((n) => [n.cidr, n.label])) ||
    JSON.stringify([...granted.value].sort()) !== JSON.stringify(c.connectors.map((x) => x.id).sort())
  )
})

const { run, ask, done } = useActions()
async function save() {
  await run(
    () =>
      api.connectors.updateCaller(id.value, {
        name: name.value.trim(),
        description: description.value.trim() || null,
        enabled: enabled.value,
        networks: networks.value.filter((n) => n.cidr.trim()),
        connectorIds: granted.value,
      }),
    { success: 'Saved', invalidate: [keys.connectors] },
  )
}

// The key: fetched only when asked for, and every fetch is an audit entry.
const key = ref<string | null>(null)
async function showKey() {
  const k = await run(() => api.connectors.key(id.value), { failure: 'The key can’t be shown' })
  if (k) key.value = k.key
}
async function rotate() {
  const names = caller.data.value?.connectors.map((c) => c.name).join(', ') || 'no connector yet'
  if (
    !(await ask(
      'Rotate the key?',
      `The old key stops at once, for ${names}, until the caller has the new one.`,
      'Rotate',
    ))
  )
    return
  const k = await run(() => api.connectors.rotateKey(id.value), { success: 'A new key', invalidate: [keys.connectors] })
  if (k) key.value = k.key
}
async function remove() {
  if (!(await ask('Delete the caller?', 'Its key stops at once; its calls stay in the history.', 'Delete'))) return
  if (await done(() => api.connectors.removeCaller(id.value), { success: 'Deleted', invalidate: [keys.connectors] })) {
    await router.push({ name: 'connectors', params: { tab: 'callers' } })
  }
}
const listCommand = computed(
  () => `curl -sS ${window.location.origin}/connector/v1 -H "Authorization: Bearer $PNMT_KEY"`,
)
</script>

<template>
  <div class="page">
    <PageHeader
      :title="caller.data.value?.name ?? 'Caller'"
      :crumbs="[{ label: '3rd Party Connectors', to: { name: 'connectors', params: { tab: 'callers' } } }]"
    >
      <Button v-if="canManage" label="Delete…" text severity="danger" @click="remove" />
    </PageHeader>
    <ErrorState v-if="caller.isError.value" :error="caller.error.value" what="the caller" @retry="caller.refetch()" />
    <template v-else-if="caller.data.value">
      <section class="panel panel-body" aria-label="Key">
        <h3>Key</h3>
        <p class="small muted">
          One key per caller: it opens every connector granted below, from the networks below. Showing or copying it is
          recorded in the audit log.
        </p>
        <div v-if="key" class="key"><CopyField :value="key" label="The key" /></div>
        <div v-else class="key">
          <code>pnmt_cx_••••••••••••••••••••••••••••••••••…{{ caller.data.value.keyHint }}</code>
          <Button v-if="canManage" label="Show" icon="pi pi-eye" size="small" text @click="showKey" />
        </div>
        <p class="small muted">Issued <RelativeTime :value="caller.data.value.keyIssuedAt" />.</p>
        <Button
          v-if="canManage"
          label="Rotate…"
          icon="pi pi-refresh"
          size="small"
          severity="secondary"
          @click="rotate"
        />
      </section>

      <section class="panel panel-body" aria-label="Settings">
        <h3>Caller</h3>
        <div class="form">
          <div class="form-grid">
            <div class="field">
              <label for="caller-name">Name</label>
              <InputText id="caller-name" v-model="name" :disabled="!canManage" />
            </div>
            <div class="field">
              <label for="caller-enabled">Enabled</label>
              <ToggleSwitch v-model="enabled" input-id="caller-enabled" :disabled="!canManage" />
              <small class="field-hint">Needs at least one network.</small>
            </div>
          </div>
          <div class="field">
            <label for="caller-description">Description</label>
            <Textarea id="caller-description" v-model="description" rows="2" auto-resize :disabled="!canManage" />
          </div>
          <div class="field">
            <label>Networks it calls from</label>
            <NetworksEditor v-model="networks" :inside="caller.data.value.networks.map((n) => n.inside)" />
            <small v-if="caller.data.value.yourAddress" class="field-hint"
              >Your browser comes from {{ caller.data.value.yourAddress }}, as PNMT sees it.</small
            >
            <Message v-if="caller.data.value.addressHint" severity="warn" :closable="false" size="small">{{
              caller.data.value.addressHint
            }}</Message>
          </div>
          <div class="field">
            <label for="caller-connectors">Connectors it may call</label>
            <MultiSelect
              v-if="canManage"
              v-model="granted"
              input-id="caller-connectors"
              :options="connectors.data.value?.items ?? []"
              option-label="name"
              option-value="id"
              filter
              display="chip"
              placeholder="None"
            />
            <ul class="granted">
              <li v-for="c in caller.data.value.connectors" :key="c.id">
                <RouterLink :to="{ name: 'connector', params: { id: c.id } }">{{ c.name }}</RouterLink>
                <span class="muted small"> · {{ typeLabel(c.type) }} · /connector/v1/{{ c.slug }}</span>
                <Tag v-if="!c.enabled" value="disabled" severity="secondary" class="small-tag" />
              </li>
            </ul>
          </div>
          <div v-if="canManage" class="actions">
            <Button label="Save" icon="pi pi-check" :disabled="!dirty" @click="save" />
          </div>
        </div>
      </section>

      <section class="panel panel-body" aria-label="Calls">
        <h3>Calls</h3>
        <p>
          {{ caller.data.value.calls24h }} calls in 24 h<template v-if="caller.data.value.refused24h"
            >, {{ caller.data.value.refused24h }} refused</template
          >; the last <RelativeTime :value="caller.data.value.lastCallAt" empty="never" />
          <span v-if="caller.data.value.lastCallFrom" class="mono"> from {{ caller.data.value.lastCallFrom }}</span
          >.
          <RouterLink :to="{ name: 'connectors', params: { tab: 'calls' }, query: { caller: id } }"
            >Its calls</RouterLink
          >
        </p>
      </section>

      <section class="panel panel-body" aria-label="Usage">
        <h3>Usage</h3>
        <p class="small">The key goes in a header, never in the URL. The connectors it may call:</p>
        <CopyField :value="listCommand" label="List the connectors" />
        <p class="small muted">Each connector's <em>Usage</em> tab has the calls with their arguments.</p>
      </section>
    </template>
  </div>
</template>

<style scoped>
.panel {
  margin-bottom: var(--pnmt-space-3);
}

.key {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  margin: var(--pnmt-space-2) 0;
}

.granted {
  margin: var(--pnmt-space-2) 0 0;
  padding-left: var(--pnmt-space-4);
}

.actions {
  display: flex;
  justify-content: flex-end;
}

.small-tag {
  font-size: 0.75rem;
  padding: 0 var(--pnmt-space-1);
}

.small {
  font-size: 0.857rem;
}
</style>
