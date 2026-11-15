<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import type { Schemas as S } from '@/api/client'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { useSession } from '@/stores/session'
import { tokenKindLabel, tokenSeverity, tokenStateText } from './mcpFormat'

/**
 * Every user's tokens (docs/mcp-plan.md §10.3): blocked first, then the ones on. No value, no turning on: an admin can
 * only turn a token off, with a reason its user reads, and never act as someone else.
 */
const session = useSession()
const notify = useNotify()
const queries = useQueryClient()
const tokens = useQuery({ queryKey: [...keys.mcp, 'tokens'], queryFn: api.mcp.tokens, refetchInterval: 30_000 })
const canManage = computed(() => session.can('Mcp', 'Manage'))
const target = ref<S['McpAdminTokenItem'] | null>(null)
const reason = ref('')

async function turnOff() {
  if (!target.value) return
  try {
    await api.mcp.turnOffToken(target.value.id, reason.value)
    notify.success('Turned off')
    target.value = null
    reason.value = ''
    await queries.invalidateQueries({ queryKey: keys.mcp })
  } catch (e) {
    notify.error(e, 'Not turned off')
  }
}
</script>

<template>
  <ErrorState v-if="tokens.isError.value" :error="tokens.error.value" what="the tokens" @retry="tokens.refetch()" />
  <section v-else class="panel">
    <table class="tokens">
      <thead>
        <tr>
          <th>User</th>
          <th>Token</th>
          <th>State</th>
          <th>Last used</th>
          <th>Strikes</th>
          <th />
        </tr>
      </thead>
      <tbody>
        <tr v-for="t in tokens.data.value ?? []" :key="t.id">
          <td>
            {{ t.displayName }} <span class="muted">{{ t.username }}</span>
          </td>
          <td>
            {{ tokenKindLabel(t.token.kind) }}<code v-if="t.token.hint" class="muted"> …{{ t.token.hint }}</code>
          </td>
          <td>
            <Tag :value="tokenStateText(t.token)" :severity="tokenSeverity(t.token)" />
            <span v-if="t.token.until && t.token.on" class="muted small">
              until <RelativeTime :value="t.token.until"
            /></span>
            <div v-if="t.token.endedNote" class="muted small">{{ t.token.endedNote }}</div>
          </td>
          <td class="small">
            <template v-if="t.token.lastUsedAt"
              ><RelativeTime :value="t.token.lastUsedAt" /> · {{ t.token.lastClient ?? '' }} ·
              {{ t.token.lastUsedFrom ?? '' }}</template
            ><span v-else class="muted">never</span>
          </td>
          <td>{{ t.token.strikes || '' }}</td>
          <td class="actions">
            <RouterLink :to="{ name: 'admin-mcp', params: { tab: 'log' }, query: { token: t.id } }">Log</RouterLink>
            <Button
              v-if="canManage && t.token.on"
              size="small"
              text
              severity="danger"
              label="Turn off…"
              @click="target = t"
            />
          </td>
        </tr>
      </tbody>
    </table>
  </section>
  <Dialog
    :visible="!!target"
    modal
    header="Turn this token off?"
    :style="{ width: '28rem' }"
    @update:visible="(v: boolean) => !v && (target = null)"
  >
    <p>
      {{ target?.displayName }}'s {{ target?.token.kind === 'Write' ? 'read & write' : 'read-only' }} token stops at
      once. They read your reason.
    </p>
    <InputText v-model="reason" placeholder="Why" fluid />
    <div class="dialog-actions">
      <Button label="Cancel" text severity="secondary" @click="target = null" />
      <Button label="Turn off" severity="danger" :disabled="reason.trim().length < 3" @click="turnOff" />
    </div>
  </Dialog>
</template>

<style scoped>
.tokens {
  width: 100%;
  border-collapse: collapse;
}

.tokens th,
.tokens td {
  text-align: left;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
  vertical-align: top;
}

.actions {
  white-space: nowrap;
  text-align: right;
}

.dialog-actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--pnmt-space-2);
  margin-top: var(--pnmt-space-3);
}

.small {
  font-size: 0.857rem;
}
</style>
