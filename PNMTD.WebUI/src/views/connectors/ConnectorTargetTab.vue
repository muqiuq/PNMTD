<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import { computed, ref, watch } from 'vue'
import { api, connectorRequest, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import TargetForm from './TargetForm.vue'

/**
 * A connector's target (docs/connectors-plan.md §13.5): the fields of §3.3 as text with their selectors, and the parse. Saving a
 * changed text counts its revision up, and the arguments follow the new parameters by name (§5.5); what that does is said first.
 */
const props = defineProps<{ connector: S['ConnectorDetail'] }>()
const session = useSession()
const target = ref<S['ConnectorTargetRequest']>(connectorRequest(props.connector).target)
watch(
  () => props.connector,
  (c) => (target.value = connectorRequest(c).target),
)
const parsed = ref<S['ConnectorParseResult'] | null>(null)
const original = computed(() => connectorRequest(props.connector).target)
const dirty = computed(() => JSON.stringify(target.value) !== JSON.stringify(original.value))
const textChanged = computed(() => target.value.scriptText !== props.connector.target.scriptText)
/** What the save does to the arguments, when the new parse is there already (§5.5). */
const effects = computed(() => {
  if (!textChanged.value && target.value.function === props.connector.target.function) return []
  const p = parsed.value?.parsed
  if (!p) return ['The parameters are pending: the arguments follow them once an agent parsed the text.']
  const parameters = target.value.function
    ? (p.functions?.find((f) => f.name === target.value.function)?.parameters ?? [])
    : p.parameters
  const before = new Map(props.connector.arguments.map((a) => [a.name.toLowerCase(), a]))
  const lines: string[] = []
  for (const q of parameters) {
    if (!before.has(q.name.toLowerCase()))
      lines.push(`${q.name} is new: ${q.mandatory ? 'exposed, because it is mandatory' : 'omitted'}.`)
  }
  const names = new Set(parameters.map((q) => q.name.toLowerCase()))
  for (const a of props.connector.arguments)
    if (!names.has(a.name.toLowerCase())) lines.push(`${a.name} is gone: its row goes.`)
  return lines
})
const { run } = useActions()
async function save() {
  await run(
    () =>
      api.connectors.update(
        props.connector.id,
        connectorRequest(props.connector, { target: { ...target.value, function: target.value.function || null } }),
      ),
    {
      success: textChanged.value ? `Saved as revision ${props.connector.target.scriptRevision + 1}` : 'Saved',
      invalidate: [keys.connectors],
    },
  )
}
</script>

<template>
  <section class="panel panel-body">
    <p class="small muted">
      Revision {{ connector.target.scriptRevision }} · SHA-256
      <span class="mono">{{ connector.target.hash.slice(0, 16) }}…</span>
    </p>
    <TargetForm v-model="target" :type="connector.type" @parsed="(p) => (parsed = p)" />
    <Message v-if="dirty && effects.length" severity="info" :closable="false" size="small">
      Saving this:
      <div v-for="e in effects" :key="e">{{ e }}</div>
    </Message>
    <div v-if="session.can('Connectors', 'Manage')" class="actions">
      <Button label="Save" icon="pi pi-check" :disabled="!dirty" @click="save" />
    </div>
  </section>
</template>

<style scoped>
.actions {
  display: flex;
  justify-content: flex-end;
  margin-top: var(--pnmt-space-3);
}

.small {
  font-size: 0.857rem;
}
</style>
