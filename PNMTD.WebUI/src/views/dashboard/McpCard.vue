<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Tag from 'primevue/tag'
import { useConfirm } from 'primevue/useconfirm'
import { computed, ref } from 'vue'
import type { Schemas } from '@/api/client'
import { api, keys } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'

/**
 * MCP access (docs/mcp-plan.md §4.5): the user's two tokens, for an AI client of their own. Read-only stays on until a week
 * after they were last here; read & write for 12 hours. The value is shown once, when it is made; regenerating makes a new
 * one. Deleting needs a window of 10 minutes, opened here. Shown only while Mcp__Enabled is on.
 */
type Token = Schemas['McpTokenItem']
type Kind = Schemas['McpTokenKind']

const notify = useNotify()
const confirm = useConfirm()
const queries = useQueryClient()
const account = useQuery({ queryKey: [...keys.mcp, 'account'], queryFn: api.mcp.account, refetchInterval: 30_000 })
const a = computed(() => (account.data.value?.enabled ? account.data.value : undefined))
const lastUse = useQuery({
  queryKey: [...keys.mcp, 'mine'],
  queryFn: () => api.mcp.myLog({ page: 1, pageSize: 1 }),
  enabled: computed(() => !!a.value),
  refetchInterval: 60_000,
})
const busy = ref<string | null>(null)
const shown = ref<{ kind: Kind; value: string } | null>(null)

const label = (kind: Kind) => (kind === 'Write' ? 'Read & write' : 'Read-only')
const kindOf = (t: Token): Kind => t.kind

function stateText(t: Token): string {
  switch (t.state) {
    case 'on':
      return 'On'
    case 'blocked':
      return 'Blocked after refused requests'
    case 'expired':
      return t.kind === 'Write' ? 'Off: its 12 hours are over' : "Off: you weren't here for a week"
    case 'stamp':
      return 'Off: your password, 2FA or sessions changed'
    case 'totp':
      return 'Needs 2FA on your account'
    default:
      return t.endedWhy === 'Admin' ? 'Turned off by an admin' : 'Off'
  }
}

const severity = (t: Token) => (t.on ? 'success' : t.state === 'blocked' ? 'danger' : 'secondary')

async function run(key: string, action: () => Promise<unknown>, done?: string) {
  busy.value = key
  try {
    const result = await action()
    if (result && typeof result === 'object' && 'value' in result && typeof result.value === 'string' && result.value) {
      shown.value = { kind: key.startsWith('Write') ? 'Write' : 'Read', value: result.value }
    } else if (done) {
      notify.success(done)
    }
    await queries.invalidateQueries({ queryKey: keys.mcp })
  } catch (e) {
    notify.error(e, 'Not done')
  } finally {
    busy.value = null
  }
}

function regenerate(kind: Kind) {
  confirm.require({
    header: `A new ${label(kind).toLowerCase()} token?`,
    message: 'Clients using the current value stop working at once. The new value is shown once.',
    icon: 'pi pi-refresh',
    acceptProps: { label: 'Regenerate', severity: 'danger' },
    rejectProps: { label: 'Cancel', severity: 'secondary', text: true },
    accept: () => run(`${kind}-regenerate`, () => api.mcp.regenerate(kind)),
  })
}

function allowDeleting() {
  const minutes = a.value?.deleteMinutes ?? 10
  confirm.require({
    header: `Allow deleting for ${minutes} minutes?`,
    message: `For the next ${minutes} minutes, an AI using your read & write token can delete hosts without sensors, sensors and mail input rules, at most ${a.value?.deletesPerWindow ?? 20}. Each deletion is in the audit log.`,
    icon: 'pi pi-exclamation-triangle',
    acceptProps: { label: 'Allow deleting', severity: 'danger' },
    rejectProps: { label: 'Cancel', severity: 'secondary', text: true },
    accept: () => run('Write-delete', () => api.mcp.openDeleteWindow(), 'Deleting allowed'),
  })
}

const claude = computed(() =>
  shown.value && a.value
    ? `claude mcp add --transport http pnmt ${a.value.url} --header "Authorization: Bearer ${shown.value.value}"`
    : '',
)
const last = computed(() => lastUse.data.value?.items[0])
</script>

<template>
  <section v-if="a" class="panel mcp-card" aria-labelledby="mcp-card-title">
    <header class="panel-header">
      <span id="mcp-card-title">MCP access</span>
      <RouterLink :to="{ name: 'admin-mcp' }" class="all">How to connect <i class="pi pi-angle-right" /></RouterLink>
    </header>
    <div class="body">
      <div v-for="t in [a.read, a.write]" :key="t.kind" class="token">
        <div class="head">
          <strong>{{ label(kindOf(t)) }}</strong>
          <Tag :value="stateText(t)" :severity="severity(t)" />
          <span v-if="t.on && t.until" class="muted small"
            >until <RelativeTime :value="t.until" />{{
              t.kind === 'Read' ? ' (a week after you were last here)' : ''
            }}</span
          >
          <code v-if="t.hint" class="muted small">pnmt_{{ t.kind === 'Write' ? 'rw' : 'ro' }}_…{{ t.hint }}</code>
        </div>
        <p v-if="t.endedWhy === 'Admin' && t.endedNote && !t.on" class="muted small">{{ t.endedNote }}</p>
        <p v-if="t.kind === 'Write' && a.writeRequiresTotp && !a.totpEnabled" class="muted small">
          The read &amp; write token needs 2FA: <RouterLink :to="{ name: 'account-security' }">turn it on</RouterLink>.
        </p>
        <div class="actions">
          <Button
            v-if="!t.on"
            size="small"
            :label="t.kind === 'Write' ? `Turn on for ${a.writeHours} hours` : 'Turn on'"
            :loading="busy === `${kindOf(t)}-on`"
            :disabled="t.kind === 'Write' && a.writeRequiresTotp && !a.totpEnabled"
            @click="run(`${kindOf(t)}-on`, () => api.mcp.turnOn(kindOf(t)), 'Turned on')"
          />
          <Button
            v-if="t.on && t.kind === 'Write'"
            size="small"
            severity="secondary"
            label="Extend"
            @click="run('Write-on', () => api.mcp.turnOn('Write'), `On for ${a.writeHours} more hours`)"
          />
          <Button
            v-if="t.on"
            size="small"
            severity="secondary"
            text
            label="Turn off"
            @click="run(`${kindOf(t)}-off`, () => api.mcp.turnOff(kindOf(t)), 'Turned off')"
          />
          <Button
            size="small"
            severity="secondary"
            text
            label="Regenerate…"
            :disabled="!t.hasValue"
            @click="regenerate(kindOf(t))"
          />
        </div>
        <div v-if="t.kind === 'Write' && t.on" class="delete small">
          <template v-if="t.deleteUntil">
            <Tag value="Deleting allowed" severity="warn" />
            until <RelativeTime :value="t.deleteUntil" /> · {{ t.deleteCount }} of {{ a.deletesPerWindow }} deleted
            <Button
              size="small"
              text
              label="Stop now"
              @click="run('Write-delete', () => api.mcp.closeDeleteWindow(), 'Deleting stopped')"
            />
          </template>
          <template v-else>
            <span class="muted">Deleting: not allowed</span>
            <Button
              size="small"
              text
              severity="danger"
              :label="`Allow deleting for ${a.deleteMinutes} minutes…`"
              @click="allowDeleting"
            />
          </template>
        </div>
      </div>
      <div v-if="last" class="last muted small">
        Last used <RelativeTime :value="last.at" /> · {{ last.client ?? 'a client' }} · {{ last.tool ?? last.method }} ·
        {{ last.outcome }}
        <RouterLink :to="{ name: 'admin-mcp', params: { tab: 'log' }, query: { mine: '1' } }">Your log</RouterLink>
      </div>
    </div>

    <Dialog
      :visible="!!shown"
      modal
      header="Your new token"
      :style="{ width: '40rem' }"
      @update:visible="(v: boolean) => !v && (shown = null)"
    >
      <template v-if="shown">
        <p>
          This is the only time PNMT shows this value. Store it in your MCP client now; lost, it can only be
          regenerated.
        </p>
        <CopyField :value="shown.value" label="the token" />
        <p class="muted small">Claude Code:</p>
        <CopyField :value="claude" label="the command" />
        <p class="muted small">
          VS Code, Cursor and the others: the address {{ a.url }} with the header <code>Authorization: Bearer …</code>,
          as <RouterLink :to="{ name: 'admin-mcp' }">the documentation</RouterLink> shows.
        </p>
        <div class="dialog-actions"><Button label="I've stored it" @click="shown = null" /></div>
      </template>
    </Dialog>
  </section>
</template>

<style scoped>
.all {
  margin-left: auto;
  font-weight: 400;
  font-size: 0.929rem;
  white-space: nowrap;
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  padding: var(--pnmt-space-2) var(--pnmt-space-3) var(--pnmt-space-3);
}

.token + .token {
  border-top: 1px solid var(--pnmt-border);
  padding-top: var(--pnmt-space-2);
}

.head,
.actions,
.delete {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.actions {
  margin-top: var(--pnmt-space-1);
}

.last {
  border-top: 1px solid var(--pnmt-border);
  padding-top: var(--pnmt-space-2);
}

.dialog-actions {
  display: flex;
  justify-content: flex-end;
  margin-top: var(--pnmt-space-3);
}

.small {
  font-size: 0.857rem;
}

p {
  margin: var(--pnmt-space-1) 0;
}
</style>
