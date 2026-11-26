<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import { api, keys } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import SkipRuleDialog from './SkipRuleDialog.vue'
import { chips, draftOf, requestOf, scopeLabel, type SkipRule } from './skipRules'

/**
 * Skip rules (docs/network-suggestion-rules-plan.md §4.5): what the analysis leaves out of the suggestions, on all routers,
 * a location or one router; each with its conditions and what it skipped at the last analysis. Everyone with View sees
 * them; Manage adds, edits, turns them on and off, and deletes them.
 */
const session = useSession()
const { run, ask, done } = useActions()
const manage = computed(() => session.can('NetworkMap', 'Manage'))
const rules = useQuery({ queryKey: [...keys.network, 'skip-rules'], queryFn: () => api.network.skipRules() })
const items = computed(() => rules.data.value?.items ?? [])
const adding = ref(false)
const editing = ref<SkipRule | null>(null)
const editOpen = computed({ get: () => editing.value !== null, set: (v) => !v && (editing.value = null) })

async function toggle(rule: SkipRule, enabled: boolean) {
  await run(() => api.network.updateSkipRule(rule.id, requestOf({ ...draftOf(rule), enabled })), {
    success: enabled ? `${rule.name}: on` : `${rule.name}: off`,
    failure: 'Not changed',
    invalidate: [keys.network],
  })
}
async function remove(rule: SkipRule) {
  if (
    !(await ask(
      'Delete the skip rule?',
      `${rule.name} goes; what it skipped makes suggestions again at the next analysis. Turning it off keeps it.`,
    ))
  )
    return
  await done(() => api.network.deleteSkipRule(rule.id), { success: 'Skip rule deleted', invalidate: [keys.network] })
}
</script>

<template>
  <div class="tab">
    <div class="head">
      <p class="muted small">
        A rule leaves rows out of the suggestions: a VLAN, an interface, a type, a discovery protocol, a MAC prefix, or
        all a router shows. VLANs and bridges are put on their physical ports anyway, so one cable gives one suggestion.
      </p>
      <Button v-if="manage" label="Add rule" icon="pi pi-plus" size="small" @click="adding = true" />
    </div>
    <DataTable :value="items" :loading="rules.isPending.value" data-key="id" size="small" class="rules">
      <template #empty>
        <EmptyState
          icon="pi pi-filter-slash"
          title="No skip rules"
          :text="
            manage ? 'Add one, or use Skip like this… on a suggestion.' : 'Nothing is left out of the suggestions.'
          "
        />
      </template>
      <Column header="Name" field="name">
        <template #body="{ data }">
          <span :class="{ muted: !data.enabled }">{{ data.name }}</span>
          <Tag v-if="!data.enabled" value="off" severity="secondary" class="tag" />
        </template>
      </Column>
      <Column header="On">
        <template #body="{ data }">{{ scopeLabel(data) }}</template>
      </Column>
      <Column header="Conditions">
        <template #body="{ data }">
          <span v-if="!chips(data).length" class="muted small">everything</span>
          <Tag v-for="c in chips(data)" :key="c" :value="c" severity="secondary" class="tag mono" />
        </template>
      </Column>
      <Column header="Last analysis" style="width: 12rem">
        <template #body="{ data }">
          <span v-if="!data.enabled" class="muted small">–</span>
          <span v-else-if="data.hitsAt" class="small"
            >{{ data.hits.toLocaleString() }} row{{ data.hits === 1 ? '' : 's' }} on {{ data.hitRouters }} router{{
              data.hitRouters === 1 ? '' : 's'
            }}</span
          >
          <span v-else class="muted small">not yet analysed</span>
        </template>
      </Column>
      <Column header="Changed" style="width: 10rem">
        <template #body="{ data }">
          <RelativeTime :value="data.updatedAt ?? data.createdAt" />
          <div class="muted small">{{ data.updatedByName ?? data.createdByName ?? '' }}</div>
        </template>
      </Column>
      <Column v-if="manage" style="width: 9rem">
        <template #body="{ data }">
          <ToggleSwitch
            :model-value="data.enabled"
            :aria-label="`${data.name} on`"
            @update:model-value="(v: boolean) => toggle(data, v)"
          />
          <Button icon="pi pi-pencil" text size="small" :aria-label="`Edit ${data.name}`" @click="editing = data" />
          <Button
            icon="pi pi-trash"
            text
            size="small"
            severity="danger"
            :aria-label="`Delete ${data.name}`"
            @click="remove(data)"
          />
        </template>
      </Column>
    </DataTable>
    <SkipRuleDialog v-model:visible="adding" :types="rules.data.value?.interfaceTypes" />
    <SkipRuleDialog
      v-if="editing"
      v-model:visible="editOpen"
      :rule="editing"
      :types="rules.data.value?.interfaceTypes"
    />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.head {
  display: flex;
  gap: var(--pnmt-space-3);
  align-items: flex-start;
  justify-content: space-between;
}

.head p {
  margin: 0;
  max-width: 60rem;
}

.small {
  font-size: 0.857rem;
}

.mono {
  font-family: var(--pnmt-mono);
}

.tag {
  margin: 0 0.25rem 0.15rem 0;
}

.rules :deep(td) {
  vertical-align: top;
}
</style>
