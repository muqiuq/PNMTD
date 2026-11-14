<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import SelectButton from 'primevue/selectbutton'
import ToggleSwitch from 'primevue/toggleswitch'
import { useQuery } from '@tanstack/vue-query'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import { useActions } from '@/composables/useActions'
import LocationPicker from './LocationPicker.vue'

/**
 * *From the hosts' texts…* (docs/locations-knowledge-plan.md §3.2): the texts hosts use without a location, each with its
 * hosts. Per text, a new location of that name (at the top, or under the parent chosen here) or an existing one; both
 * assign those hosts. Texts match ignoring case and whitespace. *Clear the text on these hosts* is off by default. Which
 * texts mean the same place is for a person to say: nothing is checked in advance.
 */
const visible = defineModel<boolean>('visible', { required: true })
const emit = defineEmits<{ done: [] }>()

const suggestions = useQuery({
  queryKey: [...keys.locations, 'suggestions'],
  queryFn: api.locations.suggestions,
  enabled: visible,
})

interface Row {
  suggestion: S['LocationSuggestion']
  include: boolean
  mode: 'create' | 'pick'
  name: string
  locationId: string | null
}
const rows = ref<Row[]>([])
const parentId = ref<string | null>(null)
const clearText = ref(false)
const modes = [
  { label: 'New', value: 'create' },
  { label: 'Existing', value: 'pick' },
]

watch(
  () => [visible.value, suggestions.data.value] as const,
  ([open, list]) => {
    if (open && list) {
      rows.value = list.map((s) => ({
        suggestion: s,
        include: false,
        mode: s.matches.length ? 'pick' : 'create',
        name: s.text,
        locationId: s.matches[0]?.id ?? null,
      }))
    }
  },
  { immediate: true },
)
watch(visible, (open) => open && ((parentId.value = null), (clearText.value = false)))

const chosen = computed(() => rows.value.filter((r) => r.include))
const ready = computed(
  () =>
    chosen.value.length > 0 &&
    chosen.value.every((r) => (r.mode === 'pick' ? r.locationId !== null : r.name.trim().length >= 2)),
)
const hostCount = computed(() => chosen.value.reduce((n, r) => n + r.suggestion.hosts, 0))

const { run, busy, notify } = useActions()
async function apply() {
  const result = await run(
    () =>
      api.locations.fromText({
        items: chosen.value.map((r) =>
          r.mode === 'pick'
            ? { text: r.suggestion.text, locationId: r.locationId }
            : { text: r.suggestion.text, name: r.name.trim(), parentId: parentId.value },
        ),
        clearText: clearText.value,
      }),
    { failure: 'Not done', invalidate: [keys.locations, keys.hosts] },
  )
  if (result) {
    const created = result.locationsCreated
    notify.success(
      `${result.hostsAssigned} host${result.hostsAssigned === 1 ? '' : 's'} assigned` +
        (created ? `, ${created} location${created === 1 ? '' : 's'} created` : ''),
    )
    emit('done')
    visible.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="From the hosts' texts"
    modal
    :style="{ width: '52rem' }"
    :breakpoints="{ '959px': '95vw' }"
  >
    <p class="intro muted">
      The texts hosts use without a location. Pick the ones that are places: each becomes a location, or goes to one
      that exists, and its hosts are assigned to it.
    </p>
    <div v-if="suggestions.isPending.value" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <EmptyState
      v-else-if="!rows.length"
      icon="pi pi-check-circle"
      title="No host without a location has a location text."
    />
    <template v-else>
      <div class="options">
        <div class="field">
          <label for="from-text-parent">New locations go under</label>
          <LocationPicker
            v-model="parentId"
            input-id="from-text-parent"
            none-label="None (top level)"
            size="small"
            aria-label="New locations go under"
          />
        </div>
        <div class="switch-row">
          <ToggleSwitch v-model="clearText" input-id="from-text-clear" />
          <label for="from-text-clear">Clear the text on these hosts</label>
        </div>
      </div>
      <ul class="rows">
        <li v-for="row in rows" :key="row.suggestion.text" :class="{ chosen: row.include }">
          <Checkbox v-model="row.include" binary :input-id="`text-${row.suggestion.text}`" />
          <label :for="`text-${row.suggestion.text}`" class="text">
            <span class="strong">{{ row.suggestion.text }}</span>
            <span class="muted"> — {{ row.suggestion.hosts }} host{{ row.suggestion.hosts === 1 ? '' : 's' }}</span>
          </label>
          <SelectButton
            v-model="row.mode"
            :options="modes"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            size="small"
            :disabled="!row.include"
            :aria-label="`New or existing location for ${row.suggestion.text}`"
          />
          <InputText
            v-if="row.mode === 'create'"
            v-model="row.name"
            size="small"
            maxlength="64"
            :disabled="!row.include"
            :aria-label="`Name of the new location for ${row.suggestion.text}`"
          />
          <LocationPicker
            v-else
            v-model="row.locationId"
            size="small"
            none-label="Pick a location"
            :aria-label="`Location for ${row.suggestion.text}`"
          />
        </li>
      </ul>
      <Message v-if="chosen.length" severity="info" :closable="false" size="small">
        {{ hostCount }} host{{ hostCount === 1 ? '' : 's' }} will be assigned{{
          clearText ? ', and their texts cleared' : ''
        }}.
      </Message>
    </template>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button label="Assign" :loading="busy" :disabled="!ready" @click="apply" />
    </template>
  </Dialog>
</template>

<style scoped>
.intro {
  margin-top: 0;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}

.options {
  display: flex;
  align-items: flex-end;
  gap: var(--pnmt-space-4);
  flex-wrap: wrap;
  margin-bottom: var(--pnmt-space-3);
}

.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding-bottom: var(--pnmt-space-2);
}

.rows {
  list-style: none;
  margin: 0 0 var(--pnmt-space-3);
  padding: 0;
  max-height: 50vh;
  overflow-y: auto;
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
}

.rows li {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto minmax(10rem, 14rem);
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
}

.rows li:last-child {
  border-bottom: none;
}

.rows li:not(.chosen) .text {
  opacity: 0.75;
}

.text {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  cursor: pointer;
}

.strong {
  font-weight: 500;
}

@media (max-width: 599px) {
  .rows li {
    grid-template-columns: auto minmax(0, 1fr);
  }

  .rows li > :nth-child(n + 3) {
    grid-column: 2;
  }
}
</style>
