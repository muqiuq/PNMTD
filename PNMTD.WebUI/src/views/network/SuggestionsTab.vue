<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { useRoute } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { useSession } from '@/stores/session'
import LocationPicker from '@/views/locations/LocationPicker.vue'
import ConnectionDialog from './ConnectionDialog.vue'
import { confidenceSeverity, endLabel } from './networkFormat'
import SkipRuleDialog from './SkipRuleDialog.vue'
import { draftFromSuggestion, type RuleDraft } from './skipRules'

/**
 * Suggestions (docs/network-map-plan.md §8.5): what the scans propose, with their evidence. Make connection (with another type,
 * ports or notes), Accept as proposed, Dismiss; several at once with a selection. Only a person makes a connection. With
 * Manage, *Skip like this…* starts a skip rule from a suggestion's evidence (docs/network-suggestion-rules-plan.md §4.5).
 */
const route = useRoute()
const session = useSession()
const { run, notify } = useActions()
const state = ref<S['NetworkSuggestionState']>('Open')
const confidence = ref<S['Confidence'] | null>(null)
const locationId = ref<string | null>(null)
const device = ref<string | null>(typeof route.query.device === 'string' ? route.query.device : null)
const filters = computed(() => ({
  state: state.value,
  confidence: confidence.value ?? undefined,
  locationId: locationId.value ?? undefined,
  deviceId: device.value ?? undefined,
}))
const table = useServerTable<S['NetworkSuggestionItem']>({
  key: [...keys.network, 'suggestions'],
  fetch: (q) => api.network.suggestions(q),
  defaultSort: '-confidence',
  pageSize: 50,
  filters,
  syncUrl: true,
})
const selection = ref<S['NetworkSuggestionItem'][]>([])
const expanded = ref<Record<string, boolean>>({})
const making = ref<S['NetworkSuggestionItem'] | null>(null)
const makingOpen = computed({ get: () => making.value !== null, set: (v) => !v && (making.value = null) })
const edit = computed(() => session.can('NetworkMap', 'Edit'))
const manage = computed(() => session.can('NetworkMap', 'Manage'))
const skipping = ref<RuleDraft | null>(null)
const skippingOpen = computed({ get: () => skipping.value !== null, set: (v) => !v && (skipping.value = null) })
const skipTypes = useQuery({
  queryKey: [...keys.network, 'skip-rules'],
  queryFn: () => api.network.skipRules(),
  enabled: computed(() => skipping.value !== null),
})
const active = computed(
  () =>
    (state.value !== 'Open' ? 1 : 0) + (confidence.value ? 1 : 0) + (locationId.value ? 1 : 0) + (device.value ? 1 : 0),
)

async function accept(s: S['NetworkSuggestionItem']) {
  await run(() => api.network.accept(s.id), {
    success: `${endLabel(s.a)} ↔ ${endLabel(s.b)}: a connection`,
    failure: 'Not accepted',
    invalidate: [keys.network],
  })
}
async function dismiss(s: S['NetworkSuggestionItem']) {
  await run(() => api.network.dismiss(s.id), {
    success: 'Dismissed',
    failure: 'Not dismissed',
    invalidate: [keys.network],
  })
}
async function restore(s: S['NetworkSuggestionItem']) {
  await run(() => api.network.restore(s.id), {
    success: 'Open again',
    failure: 'Not restored',
    invalidate: [keys.network],
  })
}
async function acceptSelected() {
  const result = await run(() => api.network.acceptMany(selection.value.map((s) => s.id)), {
    failure: 'Not accepted',
    invalidate: [keys.network],
  })
  if (result) {
    selection.value = []
    notify.success(
      result.refused.length
        ? `${result.done} made connections; ${result.refused.length} need a decision of their own: ${result.refused[0]!.reason}`
        : `${result.done} made connections`,
    )
  }
}
async function dismissSelected() {
  const result = await run(() => api.network.dismissMany(selection.value.map((s) => s.id)), {
    failure: 'Not dismissed',
    invalidate: [keys.network],
  })
  if (result) {
    selection.value = []
    notify.success(`${result.done} dismissed`)
  }
}
const states = [
  { label: 'Open', value: 'Open' },
  { label: 'Dismissed', value: 'Dismissed' },
  { label: 'Accepted', value: 'Accepted' },
]
const confidences = [
  { label: 'Any confidence', value: null },
  { label: 'High', value: 'High' },
  { label: 'Medium', value: 'Medium' },
  { label: 'Low', value: 'Low' },
]
</script>

<template>
  <div class="tab">
    <FilterBar v-model:search="table.search.value" placeholder="Search names, ports, evidence" :active="active">
      <Select
        v-model="state"
        :options="states"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="State"
      />
      <Select
        v-model="confidence"
        :options="confidences"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Confidence"
      />
      <LocationPicker v-model="locationId" none-label="All locations" size="small" aria-label="Location" />
      <Button v-if="device" label="All devices" icon="pi pi-times" size="small" text @click="device = null" />
      <template v-if="edit && state === 'Open'" #end>
        <span v-if="selection.length" class="muted">{{ selection.length }} chosen</span>
        <Button
          label="Accept selected"
          icon="pi pi-check"
          size="small"
          :disabled="!selection.length"
          @click="acceptSelected"
        />
        <Button
          label="Dismiss selected"
          icon="pi pi-times"
          size="small"
          severity="secondary"
          :disabled="!selection.length"
          @click="dismissSelected"
        />
      </template>
    </FilterBar>
    <ListTable
      v-model:selection="selection"
      v-model:expanded="expanded"
      :table="table"
      what="suggestions"
      :empty-text="state === 'Open' ? 'No open suggestions: a scan finds them.' : 'None.'"
    >
      <Column v-if="edit && state === 'Open'" selection-mode="multiple" style="width: 2.5rem" />
      <Column expander style="width: 2.5rem" />
      <Column header="Confidence" field="confidence" sortable style="width: 8rem">
        <template #body="{ data }">
          <Tag :value="data.confidence" :severity="confidenceSeverity(data.confidence)" />
          <Tag v-if="data.kind === 'Differs'" value="differs" severity="warn" class="differs" />
        </template>
      </Column>
      <Column header="From" field="a" sortable>
        <template #body="{ data }"
          >{{ data.a.name }} <span class="mono muted">{{ data.a.interface ?? '' }}</span></template
        >
      </Column>
      <Column header="To" field="b" sortable>
        <template #body="{ data }"
          >{{ data.b.name }} <span class="mono muted">{{ data.b.interface ?? '' }}</span></template
        >
      </Column>
      <Column header="Type" style="width: 10rem">
        <template #body="{ data }">{{ data.typeLabel ?? '–' }}</template>
      </Column>
      <Column header="Evidence">
        <template #body="{ data }">
          <span class="small">{{ data.evidence[0]?.text ?? '' }}</span>
          <span v-if="data.evidence.length > 1" class="muted small"> (+{{ data.evidence.length - 1 }} more)</span>
        </template>
      </Column>
      <Column header="Seen" field="lastSeen" sortable style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.lastSeenAt" /></template>
      </Column>
      <Column v-if="edit" style="width: 17rem" body-style="white-space: nowrap">
        <template #body="{ data }">
          <template v-if="data.state === 'Open'">
            <Button label="Make connection…" size="small" text @click="making = data" />
            <Button
              icon="pi pi-check"
              size="small"
              text
              :disabled="!data.type"
              :aria-label="`Accept ${endLabel(data.a)} ↔ ${endLabel(data.b)}`"
              @click="accept(data)"
            />
            <Button
              icon="pi pi-times"
              size="small"
              text
              severity="secondary"
              :aria-label="`Dismiss ${endLabel(data.a)} ↔ ${endLabel(data.b)}`"
              @click="dismiss(data)"
            />
            <Button
              v-if="manage"
              v-tooltip.left="'Skip like this…'"
              icon="pi pi-filter-slash"
              size="small"
              text
              severity="secondary"
              :aria-label="`Skip like ${endLabel(data.a)} ↔ ${endLabel(data.b)}…`"
              @click="skipping = draftFromSuggestion(data)"
            />
          </template>
          <Button v-else-if="data.state === 'Dismissed'" label="Restore" size="small" text @click="restore(data)" />
          <span v-else class="muted small">by {{ data.decidedByName ?? '–' }}</span>
        </template>
      </Column>
      <template #expansion="{ data }">
        <ul class="evidence">
          <li v-for="e in data.evidence" :key="e.text">
            <Tag :value="e.confidence" :severity="confidenceSeverity(e.confidence)" class="small" />
            <span>{{ e.text }}</span>
            <span class="muted small">({{ e.rule }})</span>
          </li>
          <li v-if="data.evidence.some((e: S['Evidence']) => e.vlans?.length)" class="muted small">
            VLANs on the same ports:
            {{ [...new Set(data.evidence.flatMap((e: S['Evidence']) => e.vlans ?? []))].join(', ') }}
          </li>
        </ul>
      </template>
      <template #mobile="{ item }">
        <div>{{ endLabel(item.a) }} ↔ {{ endLabel(item.b) }}</div>
        <div class="muted small">{{ item.confidence }} · {{ item.typeLabel ?? 'no type' }}</div>
      </template>
    </ListTable>
    <ConnectionDialog v-if="making" v-model:visible="makingOpen" :suggestion="making" />
    <SkipRuleDialog
      v-if="skipping"
      v-model:visible="skippingOpen"
      :initial="skipping"
      :types="skipTypes.data.value?.interfaceTypes"
    />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.small {
  font-size: 0.857rem;
}

.mono {
  font-family: var(--pnmt-mono);
}

.differs {
  margin-left: 0.25rem;
}

.evidence {
  margin: 0;
  padding-left: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}

.evidence li {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: baseline;
}
</style>
