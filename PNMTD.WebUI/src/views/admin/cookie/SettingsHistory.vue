<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import type { Section } from './useSection'

/**
 * A section's versions (docs/cookie-plan.md §9.3): who saved which, when, and why; each one's values as saved; and a
 * rollback, which saves an old version's values as a new version. Folded until opened.
 */
const props = defineProps<{ section: Section; title?: string }>()
const emit = defineEmits<{ rolledBack: [] }>()
const open = ref(false)
const versions = useQuery({
  queryKey: computed(() => [...keys.cookie, 'admin', 'versions', props.section]),
  queryFn: () => api.cookie.versions(props.section),
  enabled: open,
})
const newest = computed(() => versions.data.value?.[0]?.version)

const { run, ask, busy } = useActions()
const shown = ref<{ version: number; json: string } | null>(null)
const showing = computed({
  get: () => shown.value !== null,
  set: (v: boolean) => {
    if (!v) shown.value = null
  },
})
async function view(version: number) {
  const values = await run(() => api.cookie.version(props.section, version), { failure: 'Not loaded' })
  if (values !== undefined) shown.value = { version, json: JSON.stringify(values, null, 2) }
}
async function rollBack(version: number) {
  const sure = await ask(
    `Roll back to version ${version}?`,
    'Its values are saved as a new version and are in force at once. The later versions stay in the history.',
    'Roll back',
  )
  if (!sure) return
  const saved = await run(() => api.cookie.rollback(props.section, version), {
    success: `Rolled back to version ${version}`,
    failure: 'Not rolled back',
    invalidate: [keys.cookie],
  })
  if (saved) {
    shown.value = null
    emit('rolledBack')
  }
}
</script>

<template>
  <section class="panel history" :aria-label="`${title ?? 'The'} history`">
    <div class="panel-header">
      <span
        >History<template v-if="title"> of {{ title }}</template></span
      >
      <Button
        :label="open ? 'Hide' : 'Show'"
        :icon="open ? 'pi pi-angle-up' : 'pi pi-angle-down'"
        size="small"
        severity="secondary"
        text
        class="toggle"
        :aria-expanded="open"
        @click="open = !open"
      />
    </div>
    <template v-if="open">
      <ErrorState
        v-if="versions.isError.value"
        :error="versions.error.value"
        what="the versions"
        @retry="versions.refetch()"
      />
      <p v-else-if="versions.data.value && !versions.data.value.length" class="panel-body muted none">
        Never saved: the defaults are in force.
      </p>
      <DataTable
        v-else
        :value="versions.data.value ?? []"
        :loading="versions.isFetching.value"
        size="small"
        data-key="version"
      >
        <Column header="Version" style="width: 7rem">
          <template #body="{ data }">
            {{ data.version }}
            <Tag v-if="data.version === newest" value="in force" severity="success" class="tag" />
          </template>
        </Column>
        <Column header="When" style="width: 8rem">
          <template #body="{ data }"><RelativeTime :value="data.changedAt" /></template>
        </Column>
        <Column field="changedBy" header="Who" style="width: 10rem" />
        <Column header="Note">
          <template #body="{ data }"
            ><span class="muted">{{ data.note ?? '' }}</span></template
          >
        </Column>
        <Column header="" style="width: 14rem">
          <template #body="{ data }">
            <div class="buttons">
              <Button label="View" size="small" severity="secondary" text @click="view(data.version)" />
              <Button
                v-if="data.version !== newest"
                label="Roll back to this version"
                size="small"
                severity="secondary"
                text
                :disabled="busy"
                @click="rollBack(data.version)"
              />
            </div>
          </template>
        </Column>
      </DataTable>
    </template>
    <Dialog
      v-model:visible="showing"
      :header="shown ? `Version ${shown.version}` : ''"
      modal
      :style="{ width: '44rem' }"
      :breakpoints="{ '720px': '95vw' }"
    >
      <pre v-if="shown" class="json mono">{{ shown.json }}</pre>
      <template #footer>
        <Button
          v-if="shown && shown.version !== newest"
          label="Roll back to this version"
          severity="secondary"
          :disabled="busy"
          @click="rollBack(shown.version)"
        />
        <Button label="Close" text @click="shown = null" />
      </template>
    </Dialog>
  </section>
</template>

<style scoped>
.toggle {
  margin-left: auto;
}

.none {
  margin: 0;
}

.tag {
  margin-left: var(--pnmt-space-1);
}

.buttons {
  display: flex;
  justify-content: flex-end;
  flex-wrap: wrap;
}

.json {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 60vh;
  overflow: auto;
  font-size: 0.8rem;
}
</style>
