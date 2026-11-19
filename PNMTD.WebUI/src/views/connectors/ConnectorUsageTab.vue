<script setup lang="ts">
import Message from 'primevue/message'
import { computed } from 'vue'
import type { S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import { examples, sampleArguments } from './connectorFormat'

/**
 * *Usage* (docs/connectors-plan.md §4, §13.5): everything the caller's developers need, from the connector itself: the URL and the
 * mode, the body with the exposed arguments, the answers, and curl, PowerShell and Python with the key as $PNMT_KEY, never filled in.
 */
const props = defineProps<{ connector: S['ConnectorDetail'] }>()
const d = computed(() => props.connector.description)
const ticket = computed(() => props.connector.mode === 'Ticket')
const samples = computed(() => examples(props.connector.url, sampleArguments(d.value.arguments), ticket.value))
</script>

<template>
  <div class="tab">
    <section class="panel panel-body">
      <h3>The call</h3>
      <CopyField :value="connector.url" label="The URL" />
      <p>
        <code>POST</code> with <code>Authorization: Bearer pnmt_cx_…</code>, the caller's key, never in the URL.
        {{
          ticket
            ? 'Ticket mode: the answer comes at once, 202 with the ticket; the caller asks for the outcome with it.'
            : `Wait mode: the answer comes with the outcome, within ${connector.waitSeconds} s, or else as 202 with the ticket.`
        }}
        {{
          connector.returnOutput
            ? 'The output goes back.'
            : 'The output stays in PNMT: the answer has the state, the exit code and the times.'
        }}
      </p>
      <Message v-if="connector.plainHttp" severity="warn" :closable="false" size="small">
        PNMT's address is plain HTTP: the key and the arguments travel readable. Use HTTPS where the way isn't trusted.
      </Message>
    </section>

    <section class="panel panel-body">
      <h3>Arguments</h3>
      <table v-if="d.arguments.length" class="arguments">
        <thead>
          <tr>
            <th>Name</th>
            <th>Type</th>
            <th>Required</th>
            <th>Rules</th>
            <th>Description</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="a in d.arguments" :key="a.name">
            <td class="mono">{{ a.name }}</td>
            <td>{{ a.type }}</td>
            <td>{{ a.required ? 'yes' : '' }}</td>
            <td class="small">
              <div v-if="a.pattern">
                matches <code>{{ a.pattern }}</code>
              </div>
              <div v-if="a.oneOf?.length">one of {{ a.oneOf.join(', ') }}</div>
              <div v-if="a.min !== null && a.min !== undefined">{{ a.min }} to {{ a.max }}</div>
              <div v-if="a.default">default {{ a.default }}</div>
              <div v-if="a.multiline">line breaks allowed</div>
            </td>
            <td class="small">{{ a.help }}</td>
          </tr>
        </tbody>
      </table>
      <p v-else class="muted">No arguments: the body is <code>{}</code>.</p>
      <p class="small muted">
        Anything else in arguments refuses the call, as does a value of the wrong JSON type. Every problem is listed in
        the 422's fields. Optional: <code>reference</code> (your id for the call), <code>note</code> (why),
        <code>dryRun</code>, and the header <code>Idempotency-Key</code> for safe retries.
      </p>
    </section>

    <section class="panel panel-body">
      <h3>Examples</h3>
      <div v-for="e in samples" :key="e.label" class="example">
        <h4>{{ e.label }}</h4>
        <pre class="box mono">{{ e.code }}</pre>
        <CopyField :value="e.code" :label="`The ${e.label} example`" display="Copy the example" />
      </div>
      <p class="small">
        The states, the errors and the tickets:
        <RouterLink :to="{ name: 'connectors', params: { tab: 'docs' } }">the callers' documentation</RouterLink>
        (docs/connectors-api.md).
      </p>
    </section>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.arguments {
  width: 100%;
  border-collapse: collapse;
}

.arguments th,
.arguments td {
  text-align: left;
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
  vertical-align: top;
}

.box {
  white-space: pre;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-radius: var(--p-border-radius-md);
  overflow: auto;
  font-size: 0.8rem;
  margin: 0 0 var(--pnmt-space-1);
}

.example h4 {
  margin: var(--pnmt-space-3) 0 var(--pnmt-space-1);
}

.small {
  font-size: 0.857rem;
}
</style>
