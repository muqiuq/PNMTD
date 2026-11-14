<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Drawer from 'primevue/drawer'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { logonKindLabel } from '@/views/admin/servers/agentFormat'
import { optionSeverity } from '@/views/sensors/linux/linuxFormat'
import EditKeyDialog from './EditKeyDialog.vue'
import RemoveKeysDialog from './RemoveKeysDialog.vue'
import { keyType, noteLabel, noteSeverity, reasonLabel, removalStateLabel, removalStateSeverity } from './sshKeyFormat'
import { useKeyActions } from './useKeyActions'

/**
 * A registered key (docs/linux-sensor-plan.md §11.4): the public key with copy, fingerprint, type and bits, the comments seen on
 * servers; owner, kind and note; where it is authorized (server, account, file, options, last use there, and whether PNMT may
 * remove it there or why not); the logins with it of 30 days; its history (registered, edited, retired, added or removed on
 * servers) and its removals. Edit…, Retire… or Reactivate, Remove from servers…, Delete….
 */
const keyId = defineModel<string | null>('keyId', { required: true })
const open = computed({ get: () => keyId.value !== null, set: (v) => !v && (keyId.value = null) })
const queryClient = useQueryClient()
const detail = useQuery({
  queryKey: computed(() => [...keys.sshKeys, 'detail', keyId.value]),
  queryFn: () => api.sshKeys.get(keyId.value!),
  enabled: open,
})
const d = computed(() => detail.data.value ?? null)
const k = computed(() => d.value?.key ?? null)
const actions = useKeyActions({
  changed: (fresh) => queryClient.setQueryData([...keys.sshKeys, 'detail', fresh.key.id], fresh),
  deleted: () => (keyId.value = null),
})
const { editing, removing } = actions
</script>

<template>
  <Drawer v-model:visible="open" position="right" :style="{ width: 'min(52rem, 100vw)' }">
    <template #header>
      <span class="title">
        <strong>{{ k?.name ?? '' }}</strong>
        <Tag
          v-if="k"
          :value="k.state === 'Active' ? 'active' : 'retired'"
          :severity="k.state === 'Active' ? 'success' : 'secondary'"
        />
      </span>
    </template>
    <div v-if="!d" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <div v-else-if="k" class="content">
      <div class="actions">
        <Button label="Edit…" icon="pi pi-pencil" size="small" severity="secondary" @click="editing = k" />
        <Button
          v-if="k.state === 'Active'"
          label="Retire…"
          icon="pi pi-ban"
          size="small"
          severity="secondary"
          @click="actions.retire(k)"
        />
        <Button
          v-else
          label="Reactivate"
          icon="pi pi-replay"
          size="small"
          severity="secondary"
          @click="actions.reactivate(k)"
        />
        <Button
          label="Remove from servers…"
          icon="pi pi-trash"
          size="small"
          severity="danger"
          outlined
          :disabled="!d.authorizations.length"
          @click="actions.removeFromServers(k)"
        />
        <span class="spacer" />
        <Button label="Delete…" icon="pi pi-times" size="small" severity="danger" text @click="actions.remove(k)" />
      </div>
      <div class="tags">
        <Tag v-for="n in k.notes" :key="n" :value="noteLabel(n)" :severity="noteSeverity(n)" />
        <Tag v-if="k.weakness" :value="k.weakness" severity="danger" />
      </div>
      <dl class="facts">
        <dt>Owner</dt>
        <dd>
          {{ k.owner.name }}
          <Tag v-if="k.owner.deactivated" value="deactivated" severity="warn" class="small" />
          <Tag v-if="k.owner.formerUser" value="former user" severity="secondary" class="small" />
          <span class="muted small"> · {{ k.kind === 'Person' ? 'a person' : 'a service' }}</span>
        </dd>
        <template v-if="k.note">
          <dt>Note</dt>
          <dd>{{ k.note }}</dd>
        </template>
        <dt>Key</dt>
        <dd>{{ keyType(k.type, k.bits) }}</dd>
        <dt>Fingerprint</dt>
        <dd><CopyField :value="k.fingerprint" label="Fingerprint" /></dd>
        <dt>Public key</dt>
        <dd><CopyField :value="d.publicKey" label="Public key" class="public-key" /></dd>
        <dt>Comments seen</dt>
        <dd class="small">{{ d.commentsSeen.join(', ') || '–' }}</dd>
        <dt>Registered</dt>
        <dd class="small">
          <RelativeTime :value="k.createdAt" /> by {{ k.createdByName ?? '–' }}
          <template v-if="k.updatedAt !== k.createdAt">
            · changed <RelativeTime :value="k.updatedAt" /> by {{ k.updatedByName ?? '–' }}</template
          >
          <template v-if="k.retiredAt">
            · retired <RelativeTime :value="k.retiredAt" /> by {{ k.retiredByName }}</template
          >
        </dd>
      </dl>

      <h3>Where it is authorized</h3>
      <DataTable :value="d.authorizations" size="small" data-key="id">
        <template #empty><p class="empty muted">Nowhere, as far as PNMT's servers report.</p></template>
        <Column header="Server and account">
          <template #body="{ data }">
            <RouterLink :to="{ name: 'sensor', params: { id: data.server.id, tab: 'ssh-keys' } }">{{
              data.server.name
            }}</RouterLink>
            <Tag v-if="!data.connected" value="offline" severity="secondary" class="small" />
            · <strong>{{ data.account }}</strong>
            <div class="mono small muted">{{ data.path }}:{{ data.line }}</div>
            <Tag v-if="data.clusterWide" value="cluster-wide" severity="warn" class="small" />
            <Tag
              v-if="data.protected"
              value="protected"
              severity="success"
              class="small"
              :title="data.protectedBecause ?? undefined"
            />
          </template>
        </Column>
        <Column header="Options and notes">
          <template #body="{ data }">
            <div class="tags">
              <Tag v-for="o in data.options" :key="o" :value="o" :severity="optionSeverity(o)" class="small mono" />
              <Tag v-for="n in data.notes" :key="n" :value="n" severity="warn" class="small" />
            </div>
          </template>
        </Column>
        <Column header="Last used" style="width: 9rem">
          <template #body="{ data }">
            <template v-if="data.lastUsedAt">
              <RelativeTime :value="data.lastUsedAt" />
              <div class="mono small muted">{{ data.lastUsedFrom }}</div>
            </template>
            <span v-else class="muted small">first seen <RelativeTime :value="data.firstSeenAt" /></span>
          </template>
        </Column>
        <Column header="PNMT may remove it" style="width: 12rem">
          <template #body="{ data }">
            <Tag v-if="data.canRemove" value="yes" severity="info" class="small" />
            <span v-else class="small muted">{{ data.cannotRemove ?? 'no' }}</span>
          </template>
        </Column>
      </DataTable>

      <div class="two">
        <section>
          <h3>Logins with it, 30 days</h3>
          <ul class="plain small">
            <li v-for="l in d.recentLogins" :key="l.id">
              <RelativeTime :value="l.lastAt ?? l.at" /> · {{ l.server.name }} · <strong>{{ l.user }}</strong> ·
              {{ logonKindLabel(l.kind)
              }}<template v-if="l.sourceAddress">
                from <span class="mono">{{ l.sourceAddress }}</span></template
              ><template v-if="l.count > 1"> · {{ l.count }}×</template>
            </li>
            <li v-if="!d.recentLogins.length" class="muted">None.</li>
          </ul>
        </section>
        <section>
          <h3>History</h3>
          <ul class="plain small">
            <li v-for="(h, i) in d.history" :key="i">
              <RelativeTime :value="h.at" /> · {{ h.text }}
              <span v-if="h.by" class="muted"> ({{ h.by }})</span>
              <span v-if="h.server" class="muted"> · {{ h.server.name }}</span>
            </li>
            <li v-if="!d.history.length" class="muted">None.</li>
          </ul>
        </section>
      </div>

      <template v-if="d.removals.length">
        <h3>Removals</h3>
        <ul class="plain small">
          <li v-for="r in d.removals" :key="r.id">
            <RelativeTime :value="r.requestedAt" /> · {{ r.serverName }} · {{ r.account }} ·
            <Tag :value="removalStateLabel(r.state)" :severity="removalStateSeverity(r.state)" class="small" />
            <span class="muted">
              {{ reasonLabel(r.reason)
              }}<template v-if="r.requestedByName">, by {{ r.requestedByName }}</template></span
            >
            <div v-if="r.message" class="muted">{{ r.message }}</div>
          </li>
        </ul>
      </template>
    </div>
    <EditKeyDialog v-model:item="editing" @saved="detail.refetch()" />
    <RemoveKeysDialog v-model:source="removing" />
  </Drawer>
</template>

<style scoped>
.title {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  font-size: 1.1rem;
}

.content {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.content h3 {
  margin: var(--pnmt-space-2) 0 0;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}

.actions,
.tags {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
  align-items: center;
}

.spacer {
  flex: 1;
}

.public-key :deep(.value) {
  font-size: 0.786rem;
}

.two {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(18rem, 1fr));
  gap: var(--pnmt-space-3);
}

.plain {
  list-style: none;
  margin: var(--pnmt-space-1) 0 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.empty {
  margin: 0;
  padding: var(--pnmt-space-2) 0;
}

.small {
  font-size: 0.857rem;
}
</style>
