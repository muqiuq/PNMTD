<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import ErrorState from '@/components/ErrorState.vue'

/**
 * The MCP server's documentation (docs/mcp-plan.md §10.1): how to connect, the rules with this installation's values,
 * and every tool with what it needs, all from the server's own catalog, so it can't drift from the code.
 */
const catalog = useQuery({ queryKey: [...keys.mcp, 'catalog'], queryFn: api.mcp.catalog })
const c = computed(() => catalog.data.value)
const search = ref('')
const kinds = {
  Read: 'reads',
  Write: 'writes',
  Action: 'acts on a device',
  Delete: 'deletes',
  Ask: 'asks Cookie',
} as const
const tools = computed(() => {
  const q = search.value.trim().toLowerCase()
  return (c.value?.tools ?? []).filter(
    (t) => !q || `${t.name} ${t.title} ${t.description} ${t.right}`.toLowerCase().includes(q),
  )
})
const params = (schema: Record<string, unknown>) =>
  Object.entries((schema.properties ?? {}) as Record<string, { type?: string; description?: string }>).map(
    ([name, p]) => ({
      name,
      type: p.type ?? '',
      description: p.description ?? '',
      required: ((schema.required as string[] | undefined) ?? []).includes(name),
    }),
  )
const claude = computed(
  () => `claude mcp add --transport http pnmt ${c.value?.url ?? ''} --header "Authorization: Bearer <your token>"`,
)
const vscode = computed(() =>
  JSON.stringify(
    {
      servers: { pnmt: { type: 'http', url: c.value?.url, headers: { Authorization: 'Bearer ${input:pnmt-token}' } } },
      inputs: [{ id: 'pnmt-token', type: 'promptString', password: true, description: 'PNMT MCP token' }],
    },
    null,
    2,
  ),
)
</script>

<template>
  <ErrorState
    v-if="catalog.isError.value"
    :error="catalog.error.value"
    what="the documentation"
    @retry="catalog.refetch()"
  />
  <div v-else-if="!c" class="loading"><ProgressSpinner style="width: 32px; height: 32px" /></div>
  <div v-else class="docs">
    <Message v-if="!c.enabled" severity="info" :closable="false"
      >The MCP server is off: an admin turns it on with Mcp__Enabled=true.</Message
    >
    <section class="panel">
      <header class="panel-header">Connecting</header>
      <div class="body">
        <p>
          Your tokens are on the dashboard's <i>MCP access</i> card. A client sends one as
          <code>Authorization: Bearer pnmt_…</code> to:
        </p>
        <CopyField :value="c.url" label="the address" />
        <p class="muted small">Claude Code:</p>
        <CopyField :value="claude" label="the command" />
        <p class="muted small">
          VS Code (<code>.vscode/mcp.json</code>); Cursor and others take the same address and header:
        </p>
        <pre class="code">{{ vscode }}</pre>
      </div>
    </section>
    <section class="panel">
      <header class="panel-header">The rules</header>
      <div class="body">
        <ul>
          <li>
            <b>Read-only</b> stays on until {{ c.readDaysAfterOnline }} days after you were last in PNMT; it only reads
            and asks.
          </li>
          <li>
            <b>Read &amp; write</b> stays on for {{ c.writeHours }} hours{{
              c.writeRequiresTotp ? ' and needs 2FA on your account' : ''
            }}.
          </li>
          <li>
            <b>Deleting</b> needs a window you open on the dashboard: {{ c.deleteMinutes }} minutes, at most
            {{ c.deletesPerWindow }} objects.
          </li>
          <li>
            Limits: {{ c.callsPerMinute }} calls a minute, {{ c.writesPerMinute }} of them changes;
            {{ c.cookieMessagesPerHour }} messages to Cookie an hour.
          </li>
          <li>
            {{ c.strikesToBlock }} refused requests within 24 hours block the token, and every admin is alerted. The log
            keeps the newest {{ c.logEntries.toLocaleString() }} entries; what changed stays in the audit log.
          </li>
          <li>
            Never over MCP: PNMT's settings, users and groups, secrets, approvals, and anything that needs a
            <i>Manage</i> level.
          </li>
        </ul>
        <table class="ceiling">
          <thead>
            <tr>
              <th>Area</th>
              <th>Read &amp; write, at most</th>
              <th>Read-only, at most</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="r in c.ceiling" :key="r.area">
              <td>{{ r.label }}</td>
              <td>{{ r.readWrite ?? '—' }}</td>
              <td>{{ r.readOnly ?? '—' }}</td>
            </tr>
          </tbody>
        </table>
        <p class="muted small">A token never has more than its user. Messages to Cookie are screened by Jev first:</p>
        <ul class="small">
          <li v-for="(q, key) in c.guardQuestions" :key="key">
            <code>{{ key }}</code> {{ q }}
          </li>
        </ul>
      </div>
    </section>
    <section class="panel">
      <header class="panel-header">
        Tools <span class="muted normal">({{ c.tools.length }})</span>
        <InputText v-model="search" placeholder="Search the tools" size="small" class="search" />
      </header>
      <div class="body tools">
        <article v-for="t in tools" :key="t.name" class="tool">
          <div class="head">
            <code class="name">{{ t.name }}</code>
            <Tag
              :value="kinds[t.kind]"
              :severity="t.kind === 'Read' ? 'secondary' : t.kind === 'Delete' ? 'danger' : 'warn'"
            />
            <Tag v-if="!t.readOnlyToken" value="read & write only" severity="contrast" />
            <Tag v-if="!t.available" value="not available now" severity="secondary" />
            <span class="muted small">Needs {{ t.right }}</span>
          </div>
          <p>{{ t.description }}</p>
          <ul v-if="params(t.parameters).length" class="params small">
            <li v-for="p in params(t.parameters)" :key="p.name">
              <code>{{ p.name }}</code> <span class="muted">{{ p.type }}{{ p.required ? ', required' : '' }}</span>
              {{ p.description }}
            </li>
          </ul>
        </article>
      </div>
    </section>
    <section class="panel">
      <header class="panel-header">What every client is told</header>
      <pre class="code body">{{ c.instructions }}</pre>
    </section>
  </div>
</template>

<style scoped>
.docs {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.body {
  padding: var(--pnmt-space-2) var(--pnmt-space-3) var(--pnmt-space-3);
}

.code {
  white-space: pre-wrap;
  font-size: 0.857rem;
  background: var(--pnmt-surface-alt, transparent);
  margin: 0;
}

.ceiling {
  border-collapse: collapse;
  margin: var(--pnmt-space-2) 0;
}

.ceiling th,
.ceiling td {
  text-align: left;
  padding: 2px var(--pnmt-space-3) 2px 0;
}

.search {
  margin-left: auto;
}

.tools {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.tool + .tool {
  border-top: 1px solid var(--pnmt-border);
  padding-top: var(--pnmt-space-2);
}

.head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.name {
  font-weight: 600;
}

.params {
  margin: 0;
  padding-left: var(--pnmt-space-4);
}

.normal {
  font-weight: 400;
}

.small {
  font-size: 0.857rem;
}

.loading {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

p {
  margin: var(--pnmt-space-1) 0;
}
</style>
