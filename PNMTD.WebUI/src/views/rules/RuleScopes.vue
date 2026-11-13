<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Message from 'primevue/message'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { importanceOptions } from '@/views/notifications/importance'

/**
 * A rule's scopes and exclusions (docs/ai-operations-plan.md §11.2, §11.4): lasting assignments to every sensor of at
 * least an importance, of a host, or every sensor, today's and those added later; and the sensors taken out of them.
 * Before saving, the preview says how many sensors the scopes would cover and how many events a day they wrote.
 */
const props = defineProps<{ rule: S['NotificationRuleDetail']; canWrite: boolean }>()
const queryClient = useQueryClient()
const { run, busy } = useActions()

type Draft = S['NotificationScopeRequest'] & { hostName?: string | null }
const draft = ref<Draft[]>([])
watch(
  () => props.rule.scopes,
  (scopes) =>
    (draft.value = (scopes ?? []).map((s) => ({
      kind: s.kind,
      importance: s.importance ?? null,
      hostId: s.host?.id ?? null,
      hostName: s.host?.name ?? null,
    }))),
  { immediate: true },
)
const changed = computed(
  () =>
    JSON.stringify(draft.value.map(({ kind, importance, hostId }) => ({ kind, importance, hostId }))) !==
    JSON.stringify(
      (props.rule.scopes ?? []).map((s) => ({
        kind: s.kind,
        importance: s.importance ?? null,
        hostId: s.host?.id ?? null,
      })),
    ),
)

const kinds: { label: string; value: S['NotificationScopeKind'] }[] = [
  { label: 'Every sensor of at least an importance', value: 'Importance' },
  { label: 'Every sensor of a host', value: 'Host' },
  { label: 'Every sensor', value: 'Everything' },
]
const newKind = ref<S['NotificationScopeKind']>('Importance')
const newImportance = ref<S['Importance']>('High')
const newHost = ref<string | null>(null)
const hosts = useQuery({
  queryKey: [...keys.hosts, 'all-for-scopes'],
  queryFn: () => api.hosts.list({ pageSize: 500, sort: 'name' }),
  enabled: computed(() => props.canWrite),
})
const hostOptions = computed(() => (hosts.data.value?.items ?? []).map((h) => ({ label: h.name, value: h.id })))

function add() {
  const host = hostOptions.value.find((h) => h.value === newHost.value)
  draft.value = [
    ...draft.value,
    {
      kind: newKind.value,
      importance: newKind.value === 'Importance' ? newImportance.value : null,
      hostId: newKind.value === 'Host' ? newHost.value : null,
      hostName: host?.label ?? null,
    },
  ]
}
const label = (s: Draft) =>
  s.kind === 'Importance'
    ? `Every sensor of importance ${s.importance} or higher`
    : s.kind === 'Host'
      ? `Every sensor of the host ${s.hostName ?? '?'}`
      : 'Every sensor'

const body = computed(() => draft.value.map(({ kind, importance, hostId }) => ({ kind, importance, hostId })))
const preview = useQuery({
  queryKey: computed(() => [...keys.rule(props.rule.id), 'scope-preview', body.value]),
  queryFn: () => api.notifications.previewScopes(props.rule.id, body.value),
  enabled: computed(() => props.canWrite && draft.value.length > 0 && changed.value),
})

async function save() {
  const done = await run(
    async () => {
      await api.notifications.setScopes(props.rule.id, body.value)
      return true
    },
    { success: 'Scopes saved', failure: 'Not saved' },
  )
  if (done) {
    await queryClient.invalidateQueries({ queryKey: [...keys.rule(props.rule.id)] })
    await queryClient.invalidateQueries({ queryKey: [...keys.rules] })
    await queryClient.invalidateQueries({ queryKey: [...keys.notifications] })
  }
}

async function include(sensor: S['LinkedSensor']) {
  await run(() => api.notifications.include(props.rule.id, sensor.sensorId), {
    success: `${sensor.sensorName} included again`,
    invalidate: [keys.rule(props.rule.id), keys.notifications],
  })
}
</script>

<template>
  <section class="panel" aria-labelledby="scopes-title">
    <div id="scopes-title" class="panel-header">Scopes</div>
    <div class="panel-body body">
      <p class="muted small">
        Besides its linked sensors, the rule notifies about every sensor its scopes cover, also those added later. A
        sensor's importance is its own, or else its host's.
      </p>
      <p v-if="!draft.length" class="muted">No scope: only the linked sensors.</p>
      <ul v-else class="list">
        <li v-for="(s, i) in draft" :key="i">
          <i class="pi pi-circle-fill dot" aria-hidden="true" />
          <span>{{ label(s) }}</span>
          <Button
            v-if="canWrite"
            icon="pi pi-times"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`Remove: ${label(s)}`"
            @click="draft = draft.filter((_, j) => j !== i)"
          />
        </li>
      </ul>
      <div v-if="canWrite" class="add">
        <Select v-model="newKind" :options="kinds" option-label="label" option-value="value" aria-label="Scope" />
        <Select
          v-if="newKind === 'Importance'"
          v-model="newImportance"
          :options="importanceOptions"
          option-label="label"
          option-value="value"
          aria-label="At least"
        />
        <Select
          v-if="newKind === 'Host'"
          v-model="newHost"
          :options="hostOptions"
          option-label="label"
          option-value="value"
          filter
          placeholder="Host"
          aria-label="Host"
        />
        <Button
          label="Add"
          icon="pi pi-plus"
          size="small"
          severity="secondary"
          :disabled="newKind === 'Host' && !newHost"
          @click="add"
        />
      </div>
      <Message v-if="canWrite && changed && preview.data.value" severity="info" :closable="false" size="small">
        These scopes cover {{ preview.data.value.sensors }} sensor(s) now, which wrote about
        {{ preview.data.value.eventsPerDay }} events a day over the last 7 days.
      </Message>
      <div v-if="canWrite && changed" class="actions">
        <Button label="Save scopes" size="small" :loading="busy" @click="save" />
        <Button
          label="Discard"
          size="small"
          text
          severity="secondary"
          @click="
            draft = (rule.scopes ?? []).map((s) => ({
              kind: s.kind,
              importance: s.importance ?? null,
              hostId: s.host?.id ?? null,
              hostName: s.host?.name ?? null,
            }))
          "
        />
      </div>

      <template v-if="rule.exclusions?.length">
        <h3 class="title">Excluded from the scopes</h3>
        <ul class="list">
          <li v-for="x in rule.exclusions" :key="x.sensorId">
            <i class="pi pi-ban muted" aria-hidden="true" />
            <RouterLink :to="{ name: 'sensor', params: { id: x.sensorId } }">{{ x.sensorName }}</RouterLink>
            <span class="muted small">{{ x.hostName }}</span>
            <Button
              v-if="canWrite"
              label="Include again"
              size="small"
              text
              :aria-label="`Include ${x.sensorName} again`"
              @click="include(x)"
            />
          </li>
        </ul>
      </template>
    </div>
  </section>
</template>

<style scoped>
.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}
.list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}
.list li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
.dot {
  font-size: 0.6rem;
}
.add {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
  align-items: center;
}
.actions {
  display: flex;
  gap: var(--pnmt-space-2);
}
.title {
  font-size: 0.93rem;
  margin: 0;
}
.small {
  font-size: 0.857rem;
}
</style>
